using System;
using System.Collections.Generic;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Data.Definitions.Campaign;
using PFE.Entities.Enemies;
using PFE.Entities.Enemies.Archetypes;
using PFE.Entities.Enemies.Bosses;
using PFE.Entities.Units;
using PFE.Systems.Audio;
using PFE.Systems.Campaign;
using PFE.Systems.Combat;
using PFE.Systems.Map.Scripting;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Physics;
using PFE.Systems.Weapons;
using UnityEngine;
using VContainer;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// Turns a room's <see cref="UnitInstance"/> records into live GameObjects.
    ///
    /// <para><b>Why this class had to exist.</b> <c>room.units</c> had consumers but no producer:
    /// <c>RoomPopulator</c> wrote the records and <c>RoomStateSnapshot</c> serialized them, and
    /// <b>nothing in the repo ever instantiated a <c>UnitController</c></b> — every reference was a
    /// <c>GetComponent&lt;UnitController&gt;()</c> in <c>Projectile</c>, <c>ThrownObject</c>,
    /// <c>TilePhysicsController</c> or a presenter. So authored enemies existed as data and were never
    /// drawn, which is why the camp's training dummies were invisible even once they were correctly
    /// classified.</para>
    ///
    /// <para>It follows <see cref="RoomObjectVisualManager"/>: one presenter GameObject per record,
    /// parented under the room, positioned in room-local pixel space, destroyed with the room.</para>
    /// </summary>
    public sealed class RoomUnitSpawner : IUnitCommandHost
    {
        /// <summary>
        /// The AS3 controller class → port type map. Keyed on the <c>cl=</c> attribute of the
        /// <b>definition</b> row, which is what AS3 reads: <c>Unit.as:702</c> reassigns its local
        /// <c>node</c> to <c>AllData.d.obj.(@id == id)[0]</c> and <c>:708</c> then reads
        /// <c>cn = node.@cl;</c> from that. The <i>placed</i> node (the one carrying <c>turn</c>) never
        /// carries <c>cl</c> — 0 of the 564 room assets under <c>Resources/Rooms</c> do.
        ///
        /// <para>One entry today, deliberately. The other ~44 <c>vis = new …</c> sites in the oracle's
        /// unit scripts are not ported, and inventing a family registry before a second brain exists
        /// would be guessing at its shape — the same mistake the ShotPlan and AI docs warn about.</para>
        /// </summary>
        static readonly Dictionary<string, Type> ControllerTypes =
            new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
            {
                // Training
                [TrainingDummyController.ControllerId] = typeof(TrainingDummyController),

                // NPCs — the one row that is not a family. `<obj id='npc' cl='UnitNPC'/>`
                // (AllData.as:4926) is what every authored NPC placement resolves to, and `vendor` and
                // `doctor` are the same class with a different persona (AllData.as:4927-4928). Until
                // this entry existed, `npc` fell through to the bare UnitController — so Calamity was
                // spawned as a collider with no art, no persona and no script verbs.
                [UnitNPCController.ControllerId] = typeof(UnitNPCController),
                ["vendor"] = typeof(UnitNPCController),
                ["doctor"] = typeof(UnitNPCController),

                // Family 10 — ArmedShooter
                [ArmedShooterController.ControllerId] = typeof(ArmedShooterController),
                [ArmedShooterController.ControllerAlias] = typeof(ArmedShooterController),
                [RaiderController.ControllerId] = typeof(RaiderController),
                [RaiderController.ControllerAlias] = typeof(RaiderController),
                [SlaverController.ControllerId] = typeof(SlaverController),
                [SlaverController.ControllerAlias] = typeof(SlaverController),
                [ZebraController.ControllerId] = typeof(ZebraController),
                [ZebraController.ControllerAlias] = typeof(ZebraController),
                [RangerController.ControllerId] = typeof(RangerController),
                [RangerController.ControllerAlias] = typeof(RangerController),
                [EnclController.ControllerId] = typeof(EnclController),
                [EnclController.ControllerAlias] = typeof(EnclController),
                [MercController.ControllerId] = typeof(MercController),
                [MercController.ControllerAlias] = typeof(MercController),
                [NecrosController.ControllerId] = typeof(NecrosController),
                [NecrosController.ControllerAlias] = typeof(NecrosController),

                // Family 11 — Undead
                [ZombieController.ControllerId] = typeof(ZombieController),
                [ZombieController.ControllerAlias] = typeof(ZombieController),
                // NOT ZombieController: UnitSpectre extends Unit, not UnitZombie, and guide 04 forbids
                // routing it to ZombieBrain (no digger, no jump, no drop-through, no resurrection).
                [SpectreController.ControllerId] = typeof(SpectreController),
                [SpectreController.ControllerAlias] = typeof(SpectreController),
                [GhoulController.ControllerId] = typeof(GhoulController),
                [GhoulController.ControllerAlias] = typeof(GhoulController),
                [DeadController.ControllerId] = typeof(DeadController),
                [DeadController.ControllerAlias] = typeof(DeadController),

                // Family 12 — Alicorn / Magic
                [AlicornController.ControllerId] = typeof(AlicornController),
                [AlicornController.ControllerAlias] = typeof(AlicornController),

                // Family 13 — Robot
                [RobobrainController.ControllerId] = typeof(RobobrainController),
                [RobobrainController.ControllerAlias] = typeof(RobobrainController),
                ["robot"] = typeof(RobobrainController),
                // UnitEqd extends UnitAIRobot (UnitEqd.as:5) — the same brain as robobrain. Registered
                // because `eqd.asset` has empty controllerId/parentId, so the chain lands on unitId
                // "eqd" and would otherwise fall through to the brain-less base UnitController (F2).
                [EqdController.ControllerId] = typeof(EqdController),
                [EqdController.ControllerAlias] = typeof(EqdController),
                [DronController.ControllerId] = typeof(DronController),
                [DronController.ControllerAlias] = typeof(DronController),
                ["dront"] = typeof(DronController),
                [ProtectController.ControllerId] = typeof(ProtectController),
                [ProtectController.ControllerAlias] = typeof(ProtectController),
                [GutsyController.ControllerId] = typeof(GutsyController),
                [GutsyController.ControllerAlias] = typeof(GutsyController),
                [SentinelController.ControllerId] = typeof(SentinelController),
                [SentinelController.ControllerAlias] = typeof(SentinelController),
                [SpriteBotController.ControllerId] = typeof(SpriteBotController),
                [SpriteBotController.ControllerAlias] = typeof(SpriteBotController),
                [VortexController.ControllerId] = typeof(VortexController),
                [VortexController.ControllerAlias] = typeof(VortexController),
                [RollerController.ControllerId] = typeof(RollerController),
                [RollerController.ControllerAlias] = typeof(RollerController),
                [MspController.ControllerId] = typeof(MspController),
                [MspController.ControllerAlias] = typeof(MspController),

                // Family 14 — Flyer
                [FlyerController.ControllerId] = typeof(FlyerController),
                [FlyerController.ControllerAlias] = typeof(FlyerController),
                [BloatController.ControllerId] = typeof(BloatController),
                [BloatController.ControllerAlias] = typeof(BloatController),
                [BatController.ControllerId] = typeof(BatController),
                [BatController.ControllerAlias] = typeof(BatController),
                ["bat"] = typeof(BatController),
                ["bloodwing"] = typeof(BatController),
                ["bloodwing2"] = typeof(BatController),
                [PhoenixController.ControllerId] = typeof(PhoenixController),
                [PhoenixController.ControllerAlias] = typeof(PhoenixController),
                [BloatEmitterController.ControllerId] = typeof(BloatEmitterController),
                [BloatEmitterController.ControllerAlias] = typeof(BloatEmitterController),
                ["ebloat"] = typeof(BloatEmitterController),

                // Family 15 — Climber
                [ClimberController.ControllerId] = typeof(ClimberController),
                [ClimberController.ControllerAlias] = typeof(ClimberController),
                [AntController.ControllerId] = typeof(AntController),
                [AntController.ControllerAlias] = typeof(AntController),
                [AntEmitterController.ControllerId] = typeof(AntEmitterController),
                [AntEmitterController.ControllerAlias] = typeof(AntEmitterController),
                ["eant"] = typeof(AntEmitterController),

                // Family 16 — Beast
                [BeastController.ControllerId] = typeof(BeastController),
                [BeastController.ControllerAlias] = typeof(BeastController),
                [MonstrikController.ControllerId] = typeof(MonstrikController),
                [MonstrikController.ControllerAlias] = typeof(MonstrikController),
                ["rat"] = typeof(MonstrikController),
                ["molerat"] = typeof(MonstrikController),
                ["scorp"] = typeof(MonstrikController),
                ["scorp1"] = typeof(MonstrikController),
                ["scorp2"] = typeof(MonstrikController),
                ["scorp3"] = typeof(MonstrikController),
                ["tarakan"] = typeof(MonstrikController),
                [HellhoundController.ControllerId] = typeof(HellhoundController),
                [HellhoundController.ControllerAlias] = typeof(HellhoundController),
                ["hellhound"] = typeof(HellhoundController),
                ["hellhound1"] = typeof(HellhoundController),
                [SlimeController.ControllerId] = typeof(SlimeController),
                [SlimeController.ControllerAlias] = typeof(SlimeController),
                ["slime"] = typeof(SlimeController),
                ["cryoslime"] = typeof(SlimeController),
                ["pinkslime"] = typeof(SlimeController),

                // Family 17 — Aquatic
                [AquaticController.ControllerId] = typeof(AquaticController),
                [AquaticController.ControllerAlias] = typeof(AquaticController),
                [FishController.ControllerId] = typeof(FishController),
                [FishController.ControllerAlias] = typeof(FishController),
                ["fish"] = typeof(FishController),
                ["fish1"] = typeof(FishController),
                ["fish2"] = typeof(FishController),
                ["fish3"] = typeof(FishController),

                // Family 18 — Trap / Stationary
                [TrapController.ControllerId] = typeof(TrapController),
                [TrapController.ControllerAlias] = typeof(TrapController),
                [TurretController.ControllerId] = typeof(TurretController),
                [TurretController.ControllerAlias] = typeof(TurretController),
                ["turret"] = typeof(TurretController),
                ["turret0"] = typeof(TurretController),
                ["turret1"] = typeof(TurretController),
                ["turret2"] = typeof(TurretController),
                ["turret3"] = typeof(TurretController),
                ["turret4"] = typeof(TurretController),
                ["turret5"] = typeof(TurretController),
                [BearTrapController.ControllerId] = typeof(BearTrapController),
                [BearTrapController.ControllerAlias] = typeof(BearTrapController),
                ["trap"] = typeof(BearTrapController),
                ["mtrap"] = typeof(BearTrapController),
                [TriggerController.ControllerId] = typeof(TriggerController),
                [TriggerController.ControllerAlias] = typeof(TriggerController),
                ["trigridge"] = typeof(TriggerController),
                ["trigplate"] = typeof(TriggerController),
                ["triglaser"] = typeof(TriggerController),
                ["trigcans"] = typeof(TriggerController),
                ["trcans"] = typeof(TriggerController),
                ["trlaser"] = typeof(TriggerController),
                ["trplate"] = typeof(TriggerController),
                ["trridge"] = typeof(TriggerController),
                [DamagerController.ControllerId] = typeof(DamagerController),
                [DamagerController.ControllerAlias] = typeof(DamagerController),
                ["damgren"] = typeof(DamagerController),
                ["damshot"] = typeof(DamagerController),
                ["damexpl1"] = typeof(DamagerController),
                ["expl1"] = typeof(DamagerController),
                [MagicWallController.ControllerId] = typeof(MagicWallController),
                [MagicWallController.ControllerAlias] = typeof(MagicWallController),
                ["mwall"] = typeof(MagicWallController),
                [TransmitterController.ControllerId] = typeof(TransmitterController),
                [TransmitterController.ControllerAlias] = typeof(TransmitterController),
                ["transm"] = typeof(TransmitterController),
                ["transmitter"] = typeof(TransmitterController),
                [DestructiblePropController.ControllerId] = typeof(DestructiblePropController),
                [DestructiblePropController.ControllerAlias] = typeof(DestructiblePropController),
                ["destr1"] = typeof(DestructiblePropController),
                ["destr"] = typeof(DestructiblePropController),

                // Bosses
                [BossController.ControllerId] = typeof(BossController),
                [BossController.ControllerAlias] = typeof(BossController),
                [BossRaiderController.ControllerId] = typeof(BossRaiderController),
                [BossRaiderController.ControllerAlias] = typeof(BossRaiderController),
                ["bossraider"] = typeof(BossRaiderController),
                [BossEnclController.ControllerId] = typeof(BossEnclController),
                [BossEnclController.ControllerAlias] = typeof(BossEnclController),
                ["bossencl"] = typeof(BossEnclController),
                [BossNecrController.ControllerId] = typeof(BossNecrController),
                [BossNecrController.ControllerAlias] = typeof(BossNecrController),
                ["bossnecr"] = typeof(BossNecrController),
                [BossAlicornController.ControllerId] = typeof(BossAlicornController),
                [BossAlicornController.ControllerAlias] = typeof(BossAlicornController),
                ["bossalicorn"] = typeof(BossAlicornController),
                [BossDronController.ControllerId] = typeof(BossDronController),
                [BossDronController.ControllerAlias] = typeof(BossDronController),
                ["bossdron"] = typeof(BossDronController),
                ["megadron"] = typeof(BossDronController),
                [BossUltraController.ControllerId] = typeof(BossUltraController),
                [BossUltraController.ControllerAlias] = typeof(BossUltraController),
                ["bossultra"] = typeof(BossUltraController),
                ["ultra"] = typeof(BossUltraController),
                [ThunderHeadController.ControllerId] = typeof(ThunderHeadController),
                [ThunderHeadController.ControllerAlias] = typeof(ThunderHeadController),
                ["thunderhead"] = typeof(ThunderHeadController),
                [ThunderTurretController.ControllerId] = typeof(ThunderTurretController),
                [ThunderTurretController.ControllerAlias] = typeof(ThunderTurretController),
                ["ttur"] = typeof(ThunderTurretController),
            };

        readonly RoomInstance _room;
        readonly Transform _parent;
        readonly IUnitDefinitionProvider _definitions;
        readonly Dictionary<UnitInstance, GameObject> _spawned = new Dictionary<UnitInstance, GameObject>();
        readonly List<UnitInstance> _stale = new List<UnitInstance>();

        /// <summary>
        /// The room's tile query, shared by every unit this spawner builds and created lazily on the
        /// first spawn. Shared rather than per-unit because it is a stateless reader over
        /// <c>RoomInstance.tiles</c> — which it reads live, so one instance stays correct as tiles are
        /// destroyed — and because constructing one per unit would rebuild a
        /// <c>TileCollisionSystem</c> for each.
        /// </summary>
        ITileQueryService _tileQuery;

        /// <summary>
        /// The port's damage authority, handed to every unit this spawner builds.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the spawner has to pass it and <c>[Inject]</c> cannot.</b> Units here are built
        /// with <c>AddComponent</c>, which VContainer never observes, so a <c>[Inject]</c> field on
        /// <c>UnitController</c> stays null for every spawned unit — the player is the only unit that
        /// gets it, because the player's GameObject is in the scene scope's
        /// <c>autoInjectGameObjects</c>. Without this seam a crate would damage an armoured enemy
        /// exactly as much as a bare one, and the only sign would be a warning in the log.</para>
        ///
        /// <para>Optional, and null is a legitimate state: the spawner is also constructed directly by
        /// tests, and a unit with no damage system falls back to raw HP damage with a loud warning. The
        /// alternative — making it required — would break every existing construction site to no
        /// benefit, since the fallback is observable.</para>
        /// </remarks>
        PFE.Systems.Combat.DamageSystem _damageSystem;

        /// <summary>
        /// The fixed-step simulation, handed to every unit this spawner builds so a motor-less NPC
        /// steps on <c>SimLoop</c> instead of Unity's <c>FixedUpdate</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>Same reason the damage system travels this way, and the same two entry points.</b>
        /// A unit is built with <c>AddComponent</c>, which VContainer never observes, so a
        /// <c>[Inject]</c> field on <c>UnitController</c> cannot reach it and the spawner is the only
        /// handover. The clock has one further reason to be optional: it arrives from
        /// <c>MapBridge</c> through <c>RoomVisualController</c>, and a spawner built directly by a test
        /// has no container at all — in which case every unit keeps the legacy <c>FixedUpdate</c>
        /// driver, which is exactly what the pre-migration tests exercise.</para>
        ///
        /// <para>Null is therefore a legitimate state, not a wiring failure. It is only a failure if
        /// <c>PfeDebugSettings.simTickUnits</c> is on and <c>MapBridge</c> had a clock to hand over —
        /// which is why the handover is gated at <c>MapBridge</c> rather than here.</para>
        /// </remarks>
        PFE.Core.SimClock _simClock;
        PFE.Core.SimLoop _simLoop;

        /// <summary>
        /// The resolver that turns an effect id into its template — handed to every unit this spawner
        /// builds, so AS3's <c>Unit.effects</c> (<c>Unit.as:494</c>) is not an empty array on a live
        /// NPC.
        /// </summary>
        /// <remarks>
        /// <para><b>Same seam as <see cref="_damageSystem"/>, and the same reason it must travel by
        /// hand.</b> A unit is built with <c>AddComponent</c>, which VContainer never observes, so
        /// <c>[Inject]</c> cannot reach it. The spawner is the only handover — and it needs a content
        /// registry to build the resolver, which arrives down the <c>MapBridge → RoomVisualController
        /// → spawner</c> chain.</para>
        ///
        /// <para><b>Null is a real state, and it is deliberately quiet.</b> A unit with no resolver
        /// keeps the resolver-less set: <c>AddEffect</c> refuses every id rather than materialising a
        /// phantom effect with no behaviour — the "plausible object that does nothing" this project
        /// keeps finding. That is the correct answer for a bare test spawn (which never has a registry),
        /// so this is <i>not</i> one of the noisy seams; a resolver that is present but resolves
        /// nothing is the observable failure, and the debug overlay reports it per unit.</para>
        /// </remarks>
        PFE.Systems.Effects.IEffectDefinitionResolver _effectResolver;

        /// <summary>
        /// The room's particle emitter, handed to every unit so its status effects can draw.
        /// </summary>
        /// <remarks>
        /// <b>The same handover shape as <see cref="_damageSystem"/>, and the same reason.</b> A unit is
        /// built with <c>AddComponent</c>, which VContainer never observes, so the emitter cannot reach
        /// it by <c>[Inject]</c> and the spawner is the only path. Null is legal — a bare test spawn, or
        /// a scene with no particle world — and leaves the unit's sink unset, which means its effects
        /// tick and deal damage but draw nothing. That is quiet by design: a headless unit is a normal
        /// state, not a fault.
        /// </remarks>
        PFE.Systems.Particles.Adapters.RoomParticleEmitter _particleEmitter;

        /// <summary>
        /// The NPC catalogue, handed to every <see cref="UnitNPCController"/> this spawner builds.
        /// </summary>
        /// <remarks>
        /// <para><b>Why an NPC unit needs a second catalogue at all.</b> Every NPC placement in the game
        /// shares one <c>&lt;unit id='npc'&gt;</c> row, so the unit definition cannot say which persona
        /// the instance is. That comes from the placement's <c>npc="calam"</c> attribute, and the persona
        /// row in <c>GameData.as:669</c> is what carries the art, the interaction type, the weapon and
        /// the dialogue table. <c>UnitNPC</c>'s constructor joins the two
        /// (<c>UnitNPC.as:79-91</c>); this is the port's half of that join.</para>
        ///
        /// <para><b>Same handover shape as <see cref="_damageSystem"/>, and the same reason.</b> A unit is
        /// built with <c>AddComponent</c>, which VContainer never observes, so <c>[Inject]</c> cannot
        /// reach it. Null is legal and falls back to the <c>Resources</c>-backed shared provider, so a
        /// spawner constructed directly by a test gets a provider that misses every id rather than a
        /// <c>NullReferenceException</c>.</para>
        /// </remarks>
        INpcDefinitionProvider _npcDefinitions;

        /// <summary>
        /// The room's object half of AS3's <c>land.uidObjs</c> table, forwarded to every
        /// <see cref="UnitNPCController"/> so a <b>dialogue</b> script's <c>@targ</c> resolves.
        /// </summary>
        /// <remarks>
        /// <para><b>A second handover, not the same one.</b> <see cref="UnitCommand"/> is this class's
        /// half of the table for the scripts the <i>room</i> runs (<c>surfStory</c>'s
        /// <c>@targ="surfCalam"</c>). A dialogue is a different script with a different context — AS3
        /// builds it as <c>new Script(node, World.w.land, owner, true)</c> (<c>NPC.as:322</c>) and it
        /// runs later, on an interaction — and it needs <i>both</i> halves, because its actions can name
        /// an object (<c>unlock targ="doorRBL1"</c>) as easily as a unit
        /// (<c>check targ="rblCalam"</c>).</para>
        ///
        /// <para><b>Retroactive, for the same reason <see cref="SetNpcDefinitions"/> is.</b> The live
        /// order is <c>RefreshAll()</c> first and <c>AreaTriggerSystem.SetUnitHost</c> after, because the
        /// host answers against spawned GameObjects — so by the time the hosts exist, every NPC in the
        /// room has already been built. A setter that only affected future spawns would leave the whole
        /// first room's dialogues target-less, which is silent from the outside.</para>
        /// </remarks>
        IScriptObjectHost _scriptObjects;

        /// <summary>The unit half of the same handover. See <see cref="_scriptObjects"/>.</summary>
        IUnitCommandHost _scriptUnits;

        /// <summary>
        /// Whether each spawned unit is also given a <see cref="TilePhysicsController"/> — its home on
        /// the hand-rolled AS3 motor (Stage C, path 6), gated by <c>PfeDebugSettings.UnitMotor</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this class is the right place for the handover.</b> Path 6 (the legacy Kinematic
        /// <c>Rigidbody2D</c>) is the <i>only</i> integration path any spawned unit has ever had —
        /// nothing anywhere adds a motor to one — so the guide's "delete path 6" is unreachable until
        /// the units are given a home, and this is the single construction site. See
        /// <c>docs/Roadmap/LLP2D_IMPLEMENTATION_GUIDE.md</c> §0's 2026-10-02 banner (decision D2).</para>
        ///
        /// <para><b>Default off, and the old path stays intact.</b> The <c>Rigidbody2D</c> and
        /// <c>BoxCollider2D</c> are built in both modes; this only <i>adds</i> the motor. That is what
        /// makes it a one-flag A/B rather than a migration, and it is the rollback switch Stage D
        /// removes.</para>
        /// </remarks>
        bool _useTileMotor;

        /// <summary>
        /// The container, used to reach the services a spawned enemy needs that <c>[Inject]</c> cannot
        /// deliver — the projectile factory and the sound service.
        /// </summary>
        /// <remarks>
        /// <para><b>One field rather than four, and that is the point.</b> The weapon and audio
        /// services are all DI singletons the spawner has no other way to see, and threading each of
        /// them as its own constructor parameter would add four more entries to a signature that is
        /// already nine long — each one a thing a future caller can forget. Resolving them here, once,
        /// keeps the seam at one parameter.</para>
        ///
        /// <para>Null is legal and is what every test construction gets: a spawner with no container
        /// builds units with no weapon and no voice, which is exactly the pre-change behaviour and is
        /// reported per unit rather than silently.</para>
        /// </remarks>
        IObjectResolver _resolver;

        /// <summary>Whether <see cref="EnsureCombatServices"/> has run. One attempt, not one per unit.</summary>
        bool _combatServicesResolved;

        IProjectileFactory _projectileFactory;
        ISoundService      _soundService;
        PfeDebugSettings   _debugSettings;
        PFE.Core.Rng.IRngService _rng;

        /// <summary>Unit ids already warned about, so a room full of them warns once each.</summary>
        readonly HashSet<string> _warnedMissingSprite = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _warnedUnknownController = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// No RNG here on purpose. Facing is the only random input a placement has, and it is resolved
        /// once by <c>RoomPopulator</c> — which owns the spawn stream — and delivered on
        /// <see cref="UnitInstance.facingDirection"/>. A presenter that rolled its own dice would draw
        /// from a different stream than the one that generated the room, and the room would stop being
        /// reproducible from its seed.
        /// </summary>
        public RoomUnitSpawner(
            RoomInstance room,
            Transform parent,
            IUnitDefinitionProvider definitions = null,
            PFE.Systems.Combat.DamageSystem damageSystem = null,
            PFE.Core.SimClock simClock = null,
            PFE.Core.SimLoop simLoop = null,
            PFE.Systems.Effects.IEffectDefinitionResolver effectResolver = null,
            PFE.Systems.Particles.Adapters.RoomParticleEmitter particleEmitter = null,
            bool useTileMotor = false,
            IObjectResolver resolver = null,
            INpcDefinitionProvider npcDefinitions = null)
        {
            _room = room;
            _parent = parent;
            _definitions = definitions ?? ResourcesUnitDefinitionProvider.Shared;
            _damageSystem = damageSystem;
            _simClock = simClock;
            _simLoop = simLoop;
            _effectResolver = effectResolver;
            _particleEmitter = particleEmitter;
            _useTileMotor = useTileMotor;
            _resolver = resolver;
            _npcDefinitions = npcDefinitions;
        }

        /// <summary>
        /// Hand the NPC catalogue to this spawner, including any NPCs it has already built.
        /// </summary>
        /// <remarks>
        /// <para><b>Retroactive, for the same reason <see cref="SetDamageSystem"/> is.</b> The normal
        /// order is a handover before the room exists, so the constructor argument covers it and this
        /// setter never fires. It exists for the other order, because a spawner that only affected
        /// future spawns would leave the first room's NPCs persona-less — no art, no interaction — while
        /// every later room worked, and that state is silent from the outside.</para>
        ///
        /// <para><b>Re-running <c>SetPlacement</c> is safe and deliberate.</b> It is idempotent: it
        /// re-reads the same placement and re-applies the same persona state, and the visibility and
        /// weapon ids it derives are the same ones. Re-doing it is what turns a provider-less NPC into a
        /// real one, which is the whole point of a retroactive setter.</para>
        /// </remarks>
        public void SetNpcDefinitions(INpcDefinitionProvider npcDefinitions)
        {
            _npcDefinitions = npcDefinitions;

            foreach (KeyValuePair<UnitInstance, GameObject> pair in _spawned)
            {
                if (pair.Value != null && pair.Value.TryGetComponent(out UnitNPCController npc))
                {
                    npc.SetNpcProvider(npcDefinitions);
                    npc.SetPlacement(_room != null && pair.Key != null ? pair.Key : null);
                }
            }
        }

        /// <summary>
        /// Hand the room's two script-lookup hosts to this spawner, including any NPCs it has already
        /// built — AS3's single <c>land.uidObjs</c> table, split by ownership in the port.
        /// </summary>
        /// <remarks>
        /// Both arguments are normally the room's <c>AreaTriggerSystem</c>, which implements
        /// <see cref="IScriptObjectHost"/>, <see cref="IUnitCommandHost"/> and
        /// <see cref="ILandScriptHost"/> — the same object <c>AreaTriggerSystem.Runner</c> passes to its
        /// own <c>CreateRunner</c>, so a room script and a dialogue script resolve <c>@targ</c>
        /// identically. Null is tolerated and is exactly the pre-change behaviour: a target-bearing
        /// dialogue action is reported once and skipped.
        /// </remarks>
        public void SetScriptHosts(IScriptObjectHost objects, IUnitCommandHost units)
        {
            _scriptObjects = objects;
            _scriptUnits = units;

            foreach (KeyValuePair<UnitInstance, GameObject> pair in _spawned)
            {
                if (pair.Value != null && pair.Value.TryGetComponent(out UnitNPCController npc))
                {
                    npc.SetScriptHosts(_scriptObjects, _scriptUnits);
                }
            }
        }

        /// <summary>
        /// The unit half of AS3's single <c>land.uidObjs</c> lookup — <c>Script.as:218-236</c> resolves
        /// every <c>@targ</c> through one table that holds objects <i>and</i> units.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the unit half lives here and the object half on <c>AreaTriggerSystem</c>.</b> The
        /// two vocabularies are owned by different layers: an object is a record on <c>room.objects</c>
        /// and a unit is a record on <c>room.units</c> whose <i>live</i> controller only this class
        /// holds. The caller (<c>ScriptRunner</c>) still tries both in one place, so the oracle's
        /// single-lookup behaviour stays observable from the script's side.</para>
        ///
        /// <para><b>A unit that is in the room but has no live GameObject is a miss, not a silent
        /// success.</b> Returning <c>true</c> for it would make the runner believe the command ran and
        /// suppress its "no object or unit matches" warning — which is precisely how the original
        /// <c>surfCalam</c> divergence stayed invisible.</para>
        ///
        /// <para><b>The room argument is checked, not ignored.</b> This spawner owns exactly one room's
        /// units, so a call for a different room must miss rather than answer with this room's units —
        /// two rooms are alive at once during any land transition.</para>
        /// </remarks>
        public bool UnitCommand(RoomInstance room, string uidOrCode, string command, string value)
        {
            if (room == null || !ReferenceEquals(room, _room) || string.IsNullOrEmpty(uidOrCode))
            {
                return false;
            }

            if (room.units == null)
            {
                return false;
            }

            for (int i = 0; i < room.units.Count; i++)
            {
                UnitInstance unit = room.units[i];
                if (unit == null || !IsNamed(unit, uidOrCode))
                {
                    continue;
                }

                if (!_spawned.TryGetValue(unit, out GameObject unitObject) || unitObject == null)
                {
                    return false;
                }

                if (!unitObject.TryGetComponent(out UnitController controller))
                {
                    return false;
                }

                return controller.Command(command, value);
            }

            return false;
        }

        /// <summary>
        /// Whether a placement answers to this uid or code — the two keys AS3's table is addressable by.
        /// </summary>
        /// <remarks>
        /// A blank key never matches: every unit placed by the random-enemy pass has an empty
        /// <c>uid</c>, and a <c>uid ?? code</c> chain that fell through on <c>""</c> would resolve a
        /// script's <c>@targ</c> to the first such unit in the room.
        /// </remarks>
        static bool IsNamed(UnitInstance unit, string uidOrCode)
        {
            if (!string.IsNullOrEmpty(unit.uid) &&
                string.Equals(unit.uid, uidOrCode, StringComparison.Ordinal))
            {
                return true;
            }

            return !string.IsNullOrEmpty(unit.code) &&
                   string.Equals(unit.code, uidOrCode, StringComparison.Ordinal);
        }

        /// <summary>
        /// Resolve the enemy combat services once, from the container.
        ///
        /// <para><b>Once, not once per unit.</b> <c>Resolve</c> is a dictionary lookup but the failure
        /// path is a thrown exception, and a room of twenty enemies should not run twenty of them.</para>
        ///
        /// <para><b>Each resolve is individually guarded.</b> A scene with no <c>SoundService</c>
        /// assigned (the container logs that itself) must still spawn armed enemies, and a scene whose
        /// container cannot supply a factory must still spawn enemies that make a noise on death. One
        /// missing registration must not take the other three down.</para>
        /// </summary>
        void EnsureCombatServices()
        {
            if (_combatServicesResolved) return;
            _combatServicesResolved = true;

            if (_resolver == null) return;

            try { _projectileFactory = _resolver.Resolve<IProjectileFactory>(); } catch { _projectileFactory = null; }
            try { _soundService      = _resolver.Resolve<ISoundService>(); }      catch { _soundService      = null; }
            try { _debugSettings     = _resolver.Resolve<PfeDebugSettings>(); }   catch { _debugSettings     = null; }
            try { _rng               = _resolver.Resolve<PFE.Core.Rng.IRngService>(); } catch { _rng         = null; }
        }

        /// <summary>
        /// Hand this spawner the container it reaches the weapon/audio services through, including any
        /// units it has already built.
        /// </summary>
        /// <remarks>
        /// <para><b>Retroactive, for the same reason <see cref="SetDamageSystem"/> is.</b> The normal
        /// order is a handover before the room exists — <c>MapBridge.Construct</c> runs, then
        /// <c>Start</c> builds the world — so the constructor argument covers it and this setter never
        /// fires. It exists for the other order, because a spawner that only armed future spawns would
        /// leave the first room's enemies mute and unarmed while every later room fought properly, and
        /// that state is silent from the outside.</para>
        ///
        /// <para><b>Re-resolving is safe and deliberate.</b> <see cref="EnsureCombatServices"/> is
        /// gated on <c>_combatServicesResolved</c>; clearing that flag makes the next call resolve
        /// again, which is what turns a null-container spawner into an armed one. A resolver handed
        /// over twice simply resolves the same singletons twice.</para>
        /// </remarks>
        public void SetCombatServices(IObjectResolver resolver)
        {
            _resolver = resolver;
            _combatServicesResolved = false;
            EnsureCombatServices();

            foreach (KeyValuePair<UnitInstance, GameObject> pair in _spawned)
            {
                if (pair.Value != null && pair.Value.TryGetComponent(out EnemyController enemy))
                {
                    enemy.SetWeaponServices(
                        _projectileFactory, _resolver, _soundService, _debugSettings, _rng);
                }
            }
        }

        public int SpawnedCount => _spawned.Count;

        /// <summary>
        /// The room-cleared decision for this room's prob, resolved per call by the caller —
        /// <c>RoomTransitionManager.ProbSessions</c>, which a land build replaces wholesale.
        ///
        /// <para><b>Why a provider and not the registry itself.</b> Holding the object would age: the
        /// next <c>SetProbContext</c> builds a new one and this spawner would keep answering about rooms
        /// that no longer exist. This is the same per-call read <c>AreaTriggerSystem.LandHost</c> makes
        /// for the same reason.</para>
        /// </summary>
        public Func<PFE.Systems.Map.Generation.ProbRoomSessions> ProbSessionsProvider { get; set; }

        /// <summary>
        /// The units already reported dead, so a death is counted once.
        ///
        /// <para><b>AS3's <c>die()</c> fires once per unit</b> (<c>Unit.as:4395-4402</c> is inside it),
        /// and the port's <c>UnitController.OnDeath</c> is reachable more than once for one unit — the
        /// class's own docs say so. A diff-based scan therefore needs its own latch, or one death would
        /// increment <c>killEn</c> twice and clear a room the player has not finished.</para>
        /// </summary>
        private readonly HashSet<UnitInstance> _reportedDeaths =
            new HashSet<UnitInstance>();

        /// <summary>
        /// AS3 <c>if(loc.prob) loc.prob.check()</c> inside <c>die()</c> (<c>Unit.as:4395-4402</c>) and
        /// <c>loc.prob.checkWave(true)</c> (<c>:4499</c>) — "a required unit just died".
        ///
        /// <para><b>Why a scan and not a death callback.</b> A unit is built with <c>AddComponent</c>, so
        /// VContainer never injects it and nothing in this port gives a spawned unit a reference back to
        /// its room — the spawner is the one place that holds both halves. The scan is the inverse of
        /// <see cref="RefreshAll"/>'s model: it reads the live controllers rather than the records, so a
        /// death is visible without a record being removed from <c>room.units</c>.</para>
        ///
        /// <para><b>Only for prob rooms.</b> A non-prob room has no session and costs one dictionary
        /// miss, which is the same cheap answer AS3's <c>if(loc.prob)</c> gives.</para>
        /// </summary>
        public void CheckDeaths()
        {
            if (_room == null || string.IsNullOrEmpty(_room.probId)) return;

            PFE.Systems.Map.Generation.ProbRoomSessions sessions = ProbSessionsProvider?.Invoke();
            if (sessions == null) return;

            PFE.Systems.Map.Generation.ProbRoomSession session = sessions.For(_room);
            if (session == null || session.State.closed) return;

            foreach (KeyValuePair<UnitInstance, GameObject> pair in _spawned)
            {
                UnitInstance record = pair.Key;
                if (record == null || _reportedDeaths.Contains(record)) continue;

                if (pair.Value == null || !pair.Value.TryGetComponent(out UnitController controller))
                {
                    continue;
                }

                if (controller.IsAlive) continue;

                _reportedDeaths.Add(record);

                // The oracle's order (Unit.as:4497-4501): scrDie fires, then the wave counter, then the
                // check. The port has no scrDie driver, so this is the two steps that are ported.
                sessions.CheckWave(_room, true);
                sessions.Check(_room);

                if (session.State.closed) return;
            }
        }

        /// <summary>
        /// Hand the damage authority to this spawner, including any units it has already built.
        /// </summary>
        /// <remarks>
        /// <para><b>Retroactive on purpose.</b> A room is usually built before the handover arrives, so
        /// a setter that only affected future spawns would leave the first room's units unarmoured —
        /// the exact "correct-but-empty until someone wires it" state this port keeps hitting. The
        /// already-spawned GameObjects are reachable through <see cref="_spawned"/>, so they are
        /// updated too.</para>
        ///
        /// <para>Replacing the spawner instead would orphan every existing unit's GameObject and spawn a
        /// duplicate of each, which is why this is a setter and not a reconstruct.</para>
        /// </remarks>
        public void SetDamageSystem(PFE.Systems.Combat.DamageSystem damageSystem)
        {
            _damageSystem = damageSystem;

            if (damageSystem == null)
            {
                return;
            }

            foreach (KeyValuePair<UnitInstance, GameObject> pair in _spawned)
            {
                if (pair.Value != null && pair.Value.TryGetComponent(out UnitController controller))
                {
                    controller.SetDamageSystem(damageSystem);
                }
            }
        }

        /// <summary>
        /// Hand the particle emitter to this spawner, including any units it has already built.
        /// </summary>
        /// <remarks>
        /// <para><b>Retroactive, and here that is the <i>common</i> path rather than the exception.</b>
        /// <c>RoomVisualController.SetParticleAdapters</c> records that MapBridge hands the particle
        /// adapters down <i>after</i> the first room may already have been built — so unlike the damage
        /// system, the constructor argument will usually be null and this setter is what actually wires
        /// the first room's units. A spawner that only affected future spawns would leave every unit in
        /// the opening room unable to draw its effects, which reads as "effects are broken" rather than
        /// "the first room missed a handover".</para>
        ///
        /// <para><b>Null propagates rather than being ignored.</b> Unlike
        /// <see cref="SetDamageSystem"/>, passing null here is meaningful: it unsets the sink on every
        /// unit, which is the honest state for a teardown where the emitter is gone.</para>
        /// </remarks>
        public void SetParticleEmitter(PFE.Systems.Particles.Adapters.RoomParticleEmitter emitter)
        {
            _particleEmitter = emitter;

            foreach (KeyValuePair<UnitInstance, GameObject> pair in _spawned)
            {
                if (pair.Value != null && pair.Value.TryGetComponent(out UnitController controller))
                {
                    controller.SetParticleEmitter(emitter);
                }
            }
        }

        /// <summary>
        /// Hand the fixed-step simulation to this spawner, including any units it has already built.
        /// </summary>
        /// <remarks>
        /// <para><b>Retroactive, for the same reason <see cref="SetDamageSystem"/> is.</b> The normal
        /// order is a handover <i>before</i> the room exists (<c>MapBridge.Construct</c> runs, then
        /// <c>Start</c> builds the world), so the constructor argument covers it and this setter never
        /// fires. It exists for the other order — a later handover, or a second room built by a spawner
        /// that was created before the clock arrived — because a setter that only affected future
        /// spawns would leave the first room's units on Unity's 50 Hz clock while the rest ran at 30,
        /// which is a difference nothing would report.</para>
        ///
        /// <para><b>Attaching twice is safe and is not a double-registration.</b>
        /// <c>SimLoop.Register</c> de-duplicates, and a unit that is already sim-driven simply keeps
        /// the same registration.</para>
        /// </remarks>
        public void AttachSimulation(PFE.Core.SimClock simClock, PFE.Core.SimLoop simLoop)
        {
            _simClock = simClock;
            _simLoop = simLoop;

            if (simClock == null || simLoop == null)
            {
                return;
            }

            foreach (KeyValuePair<UnitInstance, GameObject> pair in _spawned)
            {
                if (pair.Value != null && pair.Value.TryGetComponent(out UnitController controller))
                {
                    controller.AttachSimulation(simClock, simLoop);
                }
            }
        }

        /// <summary>
        /// Hand the effect-definition resolver to this spawner, including any units it has already
        /// built.
        /// </summary>
        /// <remarks>
        /// <para><b>Retroactive on purpose, for the same reason <see cref="SetDamageSystem"/> is.</b>
        /// The normal order is a handover before the room exists — <c>MapBridge.Construct</c> injects
        /// the registry and passes it down, then <c>Start</c> builds the world — so the constructor
        /// argument covers it. It exists for the other order, because a setter that only affected
        /// future spawns would leave the first room's units unable to resolve any effect id, and that
        /// state is silent from the outside: an enemy with a permanent burning effect simply never
        /// burns.</para>
        ///
        /// <para><b>Attaching twice is safe.</b> <c>EnsureEffects</c> rebuilds the unit's set, so the
        /// caller must hand the resolver over before any effect was added — which is what a spawner
        /// does, at <c>Initialize</c> time.</para>
        /// </remarks>
        public void SetEffectResolver(PFE.Systems.Effects.IEffectDefinitionResolver resolver)
        {
            _effectResolver = resolver;

            if (resolver == null)
            {
                return;
            }

            foreach (KeyValuePair<UnitInstance, GameObject> pair in _spawned)
            {
                if (pair.Value != null && pair.Value.TryGetComponent(out UnitController controller))
                {
                    controller.SetEffectResolver(resolver);
                }
            }
        }

        /// <summary>
        /// Spawn every unit the room has that is not already spawned, and destroy the ones it no longer
        /// has. Mirrors <c>RoomObjectVisualManager.SyncPresenters</c>, because a room's unit list is
        /// mutable (units die, save data is restored).
        /// </summary>
        public void RefreshAll()
        {
            if (_room?.units == null)
            {
                DestroyAll();
                return;
            }

            _stale.Clear();
            foreach (KeyValuePair<UnitInstance, GameObject> pair in _spawned)
            {
                _stale.Add(pair.Key);
            }

            for (int i = 0; i < _room.units.Count; i++)
            {
                UnitInstance unit = _room.units[i];
                if (unit == null)
                {
                    continue;
                }

                _stale.Remove(unit);

                if (!_spawned.ContainsKey(unit))
                {
                    _spawned[unit] = Spawn(unit);
                }
            }

            for (int i = 0; i < _stale.Count; i++)
            {
                DestroyUnit(_stale[i]);
            }

            _stale.Clear();
        }

        /// <summary>
        /// The GameObject built for a unit record, or false when the record has not been spawned (yet).
        ///
        /// <para><b>Needed by callers that add a unit at runtime and then have to talk to it in the same
        /// frame.</b> The alarm's reinforcement is the first one: AS3's <c>enemySpawn</c> calls
        /// <c>_loc5_.alarma()</c> on the unit it just created (<c>Location.as:2990-2997</c>), and the
        /// port has no handle on the GameObject otherwise — <c>RefreshAll</c> builds it inside this
        /// class.</para>
        /// </summary>
        public bool TryGetSpawnedObject(UnitInstance unit, out GameObject spawned)
        {
            spawned = null;
            return unit != null && _spawned.TryGetValue(unit, out spawned) && spawned != null;
        }

        public void DestroyAll()
        {
            foreach (KeyValuePair<UnitInstance, GameObject> pair in _spawned)
            {
                DestroyObject(pair.Value);
            }

            _spawned.Clear();
            _stale.Clear();
        }

        GameObject Spawn(UnitInstance unit)
        {
            _definitions.TryGetUnit(unit.unitId, out UnitDefinition definition);

            GameObject unitObject = new GameObject($"Unit_{unit.unitId}");
            if (_parent != null)
            {
                unitObject.transform.SetParent(_parent, false);
            }

            // Position first: a unit whose definition is missing still has to land where the room
            // authored it, so the failure is visible rather than at the origin.
            unitObject.transform.localPosition = WorldCoordinates.PixelToUnity(unit.position);

            // Order matters: UnitController.Awake does GetComponent<Collider2D>() and
            // GetComponent<Rigidbody2D>(), so both must exist before the controller is added.
            Rigidbody2D body = unitObject.AddComponent<Rigidbody2D>();
            body.bodyType = RigidbodyType2D.Kinematic;

            BoxCollider2D collider = unitObject.AddComponent<BoxCollider2D>();
            if (definition != null)
            {
                // AS3 Unit.as:1875-1876 -- `Y1 = Y - scY; Y2 = Y;`. The collision box sits ENTIRELY
                // ABOVE the origin, because the origin is the unit's feet. A box centred on the origin
                // puts half of it inside the floor tile the unit is standing on, which is what a
                // half-buried collider looks like from the physics side.
                collider.size = new Vector2(definition.Width, definition.Height);
                collider.offset = new Vector2(0f, definition.Height * 0.5f);
            }

            // The sprite lives on a CHILD, and that is load-bearing. The unit's own transform has to stay
            // on the feet — the collider above, UnitController's MovePosition and every ground/tile query
            // read it as the unit's position — while the sprite must be drawn 10px BELOW those feet
            // (AS3 `visBmp.y = -blitY + 10`). One transform cannot be both, so the visual moves and the
            // unit does not. UnitSpriteAnchor explains the pivot mismatch this compensates for.
            var visualObject = new GameObject("Visual");
            visualObject.transform.SetParent(unitObject.transform, false);

            SpriteRenderer renderer = visualObject.AddComponent<SpriteRenderer>();
            renderer.sortingLayerName = MapSortingLayers.BackgroundPhysicalObjects;
            // Same depth convention as props: lower on screen draws in front.
            renderer.sortingOrder = -Mathf.FloorToInt(unit.position.y / Mathf.Max(1f, WorldConstants.TILE_SIZE));
            ApplySprite(renderer, unit, definition);

            // The reader the importers were writing animation data for and nothing was reading. Added
            // unconditionally — it early-returns when the state has no frames, so a single-frame unit
            // keeps the resting sprite ApplySprite just drew, and a 40-cell looping `stay` starts moving.
            visualObject.AddComponent<UnitAnimator>().Initialize(definition, renderer);

            // ── The unit's home on the hand-rolled motor (Stage C, path 6) ──────────────────────
            //
            // Added BEFORE the UnitController, and that order is load-bearing: UnitController.Awake
            // does GetComponent<TilePhysicsController>() and derives its `_hasTilePhysics` predicate
            // from it, and Awake runs synchronously inside AddComponent. A motor added afterwards would
            // never be seen, `_hasTilePhysics` would stay false, and BOTH the motor and the unit's own
            // step would run every tick — the classic double-step.
            //
            // No `_room != null` guard: Spawn is only reachable from RefreshAll, which returns early
            // when `_room?.units` is null, so the room is guaranteed here. A guard would be dead code
            // whose comment claimed a failure mode that cannot occur.
            //
            // The Kinematic Rigidbody2D and BoxCollider2D built above are deliberately left in place in
            // both modes. Stage D deletes them; this flag is the rollback switch until then, and the
            // motor syncs `transform.position` itself so a Kinematic body that nothing else writes is
            // harmless.
            if (_useTileMotor)
            {
                var motor = unitObject.AddComponent<TilePhysicsController>();
                motor.MarkUnitOwned();

                if (definition != null)
                {
                    // PIXELS, not world units — and the conversion is the whole point of this line.
                    //
                    // UnitDefinition.Width/Height are world units (the field tooltip says so: "Width
                    // from 'sX' in AS3 (55px = 0.55 units)", Range(0.1f, 2f), and XMLConverter stores
                    // `sX / 100f`), which is what the BoxCollider2D above wants. ConfigureCollisionSize
                    // wants the opposite: its parameters are named widthPixels/heightPixels and the
                    // motor feeds them straight into TileCollisionMath.CheckTileCollisionAt, which
                    // divides by WorldConstants.TILE_SIZE to reach tile coordinates. Passing the world
                    // value raw made every motored unit's collision box 100x too small — 0.55 x 0.70 px
                    // for zombie0 instead of 55 x 70 — so the motor resolved tile collision against a
                    // point while the BoxCollider2D the F6 overlay draws was the right size. The two
                    // boxes must describe one body; UnitCollisionBoxMath is what makes them agree, and
                    // it is a named call rather than an inline `* 100f` so the conversion is testable.
                    motor.ConfigureCollisionSize(
                        UnitCollisionBoxMath.PixelsFromWorldUnits(definition.Width),
                        UnitCollisionBoxMath.PixelsFromWorldUnits(definition.Height));
                }

                // Room context first, then the position — the same order RepositionForRoom documents
                // (P0-2): every subsequent tick resolves tiles against currentRoom, so a stale room
                // means colliding against the old grid at the new coordinates.
                motor.SetRoom(_room);
                motor.SetUnityPosition(unitObject.transform.position);

                if (_simClock != null && _simLoop != null)
                {
                    motor.AttachSimulation(_simClock, _simLoop);
                }
            }

            string effectiveControllerId = !string.IsNullOrWhiteSpace(unit.controllerId)
                ? unit.controllerId
                : definition?.controllerId;
            if (string.IsNullOrWhiteSpace(effectiveControllerId) || !ControllerTypes.ContainsKey(effectiveControllerId))
            {
                if (!string.IsNullOrWhiteSpace(definition?.parentId) && ControllerTypes.ContainsKey(definition.parentId))
                {
                    effectiveControllerId = definition.parentId;
                }
                else
                {
                    string fallbackUnitId = unit.unitId ?? definition?.id;
                    if (!string.IsNullOrWhiteSpace(fallbackUnitId))
                    {
                        effectiveControllerId = fallbackUnitId;
                    }
                }
            }
            Type controllerType = ResolveControllerType(effectiveControllerId);
            var controller = (UnitController)unitObject.AddComponent(controllerType);

            // ...and the seam that turns an `npc` placement into a PERSONA. It has to run before the
            // weapon handover below, because the persona is what carries the weapon id
            // (`<npc id='calam' weap='sniper'/>`, GameData.as:669) and UnitNPC's own constructor
            // creates it on the line after it reads `xml.@npc` (UnitNPC.as:79-91 then :175-185).
            //
            // This is the join the port was missing entirely. An NPC's identity is split across two
            // documents — the unit row says the class is UnitNPC, the placement says which persona
            // (`npc="calam"`), and the persona carries the art, the interaction type, the weapon and the
            // dialogue table. Without it the unit has no sprite, cannot be talked to, and every one of
            // surfStory's five `@targ="surfCalam"` actions lands on a controller that does not
            // understand a single one of them.
            //
            // Same handover shape and the same reason as SetDamageSystem: AddComponent never goes
            // through VContainer, so `[Inject]` cannot reach a spawned unit and the spawner is the only
            // path. `SetRoom` travels with it because the `fly` verb's target is an AS3 tile and the
            // port's room-local Y is mirrored — the room's height is the anchor.
            if (controller is UnitNPCController npcController)
            {
                npcController.SetNpcProvider(_npcDefinitions);
                npcController.SetRoom(_room);

                // The land context a dialogue script is built with — AS3 `new Script(scr, World.w.land,
                // owner, true)`. Usually still null here (the hosts are handed over after RefreshAll, see
                // SetScriptHosts) and filled in retroactively; set here too so the order does not matter.
                npcController.SetScriptHosts(_scriptObjects, _scriptUnits);

                npcController.SetPlacement(unit);

                // The renderer, so the persona's own art can replace the shared `npc` row's (which is
                // null) and so `hide`/`show` have something to act on. Set after SetPlacement, so the
                // persona's initial hidden state is already resolved and the visibility it pushes lands
                // on a renderer that exists.
                npcController.SetVisualRenderer(renderer);
            }

            // ...and the seam that lets an NPC HOLD A WEAPON and MAKE A SOUND. Handed over here, before
            // Initialize, because an archetype equips its weapon in Initialize/OnDefinitionAssigned —
            // the first moment `Stats` is real. Same handover shape and the same reason as
            // SetDamageSystem below: AddComponent never goes through VContainer, so `[Inject]` on the
            // controller cannot reach a spawned unit.
            //
            // Without this the whole weapon stack was player-only: an enemy's `alilight` produced no
            // projectile, no muzzle flare and no report, and no enemy in the project made any sound at
            // all. Both were silent failures — the data was imported and catalogued the whole time.
            if (controller is EnemyController enemyController)
            {
                EnsureCombatServices();
                enemyController.SetWeaponServices(
                    _projectileFactory, _resolver, _soundService, _debugSettings, _rng);

                // ...and AS3's `locDifLevel`, which the weapon roll in Initialize reads. It has to be
                // handed over HERE, before Initialize, because `Unit.getXmlWeapon(param1)` is called from
                // the unit's constructor (`UnitRaider.as:257-272`) and the port mirrors that by rolling
                // in Initialize. See EnemyController.SetLocationDifficulty for why the room's difficulty
                // cannot be reached any other way at this point in the build order.
                //
                // The value is `RoomPopulator.ResolveLocationDifficulty`'s: AS3's `locDifLevel` is
                // `Location.enemyLevel` (`Land.as:983`), not `baseDifficulty`. The two are equal today
                // (RoomGenerator writes both from `template.difficultyLevel`) but the oracle adds a
                // `globalDif` term to `enemyLevel` alone, so reading the field the oracle reads is what
                // keeps this correct when that term lands.
                if (_room != null && _room.difficulty != null)
                {
                    enemyController.SetLocationDifficulty(_room.difficulty.enemyLevel);

                    // ...and AS3's `Location.earMult`, which setLocDif halves on easy difficulty
                    // (`Location.as:32526`). The port derives it in LocationDifficultyPlanner from the
                    // same `globalDif` term the oracle uses, and this is the seam that hands the room's
                    // value to the listener — without it every enemy hears at the neutral 1 and the
                    // difficulty halving is a field nothing reads. See
                    // EnemyController.SetLocationEarMultiplier for why it reaches hearing but not the
                    // alarm radius.
                    enemyController.SetLocationEarMultiplier(_room.difficulty.earMult);
                }

                // ...and AS3's `Location.visMult` — the sight twin of the earMult above, and the term
                // that was missing entirely. `EnemySensors.LocationVisiMult` was declared, multiplied
                // into `EffectiveSightRangePixels`, and assigned nowhere, so every observer in the game
                // saw at the neutral 1 while the oracle multiplied by the room's own value. The surf
                // rooms author `vis='10'`, so their sight range was a tenth of the original's — which is
                // exactly why `surfCalam`, perched at tile (40,7) with the nearest raider at (37,21),
                // never acquired a target. See EnemyController.SetLocationVisibilityMultiplier.
                if (_room != null)
                {
                    enemyController.SetLocationVisibilityMultiplier(
                        _room.environment != null ? _room.environment.visibilityMultiplier : 1f);
                }
            }

            // The seam UnitController.Initialize documents as the missing one: without it the unit has
            // no health, no <vulner> table, and logs "no UnitStats assigned" on every hit.
            //
            // ── The difficulty SETTING's HP cut (AS3 Unit.as:1130-1141) ───────────────────────────
            //
            // The oracle writes `this.hp = this.maxhp = node.@hp * this.hpmult` and then, for a
            // NON-PLAYER unit on easy, scales maxhp down and re-syncs hp:
            //
            //     if(this.fraction != F_PLAYER && World.w.game.globalDif <= 1) {
            //        if(globalDif == 0) this.maxhp *= 0.4;
            //        if(globalDif == 1) this.maxhp *= 0.7;
            //        this.hp = this.maxhp;
            //     }
            //
            // This is the only non-player HP construction site in the port, which is why the multiplier
            // belongs here rather than in the damage pipeline: scaling at the sink would make every
            // subsequent hit recompute a number the oracle froze at construction, and a heal would
            // restore to the wrong ceiling.
            //
            // `this.fraction` at :1130 is the DEFINITION's fraction — `node0 = AllData.d.unit.(@id ==
            // mid)[0]` at :997, read at :1001-1003 — not the placement's. The two differ for the camp
            // turrets: their `<unit>` row carries no `fraction` at all, so the oracle's `this.fraction`
            // is 0 and a camp turret IS scaled, even though its `<obj>` placement says
            // `fraction="100"`. Reading the placement here (which is applied later, at ApplyPlacement
            // below) would quietly exempt them.
            //
            // The port does not model AS3's per-unit `hpmult`, so the multiplier is applied to
            // `definition.health` directly; the oracle's two-step (`*= 0.4` then `hp = maxhp`) and this
            // one-step are the same number, and UnitStats' constructor already seeds hp from maxhp.
            float maxHp = definition != null ? definition.health : 0f;
            if (definition != null)
            {
                int globalDif = GlobalDifficulty.Value;
                bool isPlayerFaction = definition.fraction == FactionType.Player;
                if (DifficultyRules.ScalesUnitHp(globalDif, isPlayerFaction))
                {
                    maxHp *= DifficultyRules.EnemyHpMultiplier(globalDif);
                }
            }

            UnitStats stats = definition != null ? new UnitStats(maxHp, 100f) : null;
            controller.Initialize(definition, stats);

            // ...and the seam that lets a unit SHOW ITS SHIELD. The pool became real when the damage
            // pipeline learned to spend `UnitStats.ShitHp`, but nothing drew it: the alicorn's dome was a
            // private field on its controller that no renderer could read, so a shielded alicorn looked
            // exactly like an unshielded one. This is the reader, and it is the same missing-seam shape
            // as the weapon handover above — AddComponent never goes through VContainer, so the visual
            // has to be built here, where the body renderer and the controller both exist.
            //
            // AFTER Initialize, not before: AlicornController reads its tier from the definition's id
            // there, and the tier is what selects the dome (AS3 UnitAlicorn.as:191).
            //
            // Gated on HasSpellShieldOverlay so an ordinary NPC — whose pool is 0 for its whole life —
            // does not get a component and a per-frame poll that can never draw anything.
            if (controller.HasSpellShieldOverlay)
            {
                var shieldOverlay = visualObject.AddComponent<UnitShieldOverlay>();
                shieldOverlay.Initialize(
                    stats,
                    controller.ShieldOverlayTier,
                    controller.UsesBossShieldOverlay,
                    renderer);
            }

            // ...and this is the seam that stops it falling through the floor. A unit's groundedness is
            // AS3's `isLaz` (Unit.as:1962) — a question about the tile under its feet — and the port was
            // answering it from Unity collision callbacks instead, whose answer is marginal because the
            // seat is 1 px and 1 px IS Box2D's contact tolerance. UnitGroundProbe documents it.
            controller.SetTileQuery(ResolveTileQuery());

            // ...and the seam that lets it stand on a CRATE. Groundedness is a tile question for flat
            // ground, but a crate is not a tile — it is an ObjectInstance in a list the tile query cannot
            // see — so without this a unit standing on a crate is ungrounded, gravity applies, and
            // MovePosition on a Kinematic body walks it straight down through the crate. That is AS3's
            // Unit.checkShelf (Unit.as:2713-2741), and it is the same missing-seam shape as SetTileQuery
            // above, one layer out. Null when there is no room, which is legal: a bare test spawn keeps
            // the tile-only answer.
            if (_room != null)
            {
                controller.SetObjectPhysicsLayer(_room.ObjectPhysicsLayer);
            }

            // ...and the seam that lets a CRATE hurt it. AS3 Unit.udarBox (Unit.as:4209-4240) applies
            // its damage through `Unit.damage()`, which reads the target's vulnerability table, its
            // `skin` and its armour pool — so the hit has to go through DamageSystem, not through
            // IDamageable.TakeDamage, which is a raw HP subtraction. AddComponent never runs VContainer
            // injection, so `[Inject]` on UnitController cannot reach a spawned unit and this handover
            // is the only path. Null is legal (a test spawn, or a scene without the service) and the
            // unit says so once, loudly, rather than silently going unarmoured.
            if (_damageSystem != null)
            {
                controller.SetDamageSystem(_damageSystem);
            }

            // ...and the seam that lets it be BURNED. Every live unit owns an effect set (AS3
            // Unit.as:494 declares `effects` on the base Unit, so it is the player's and every NPC's),
            // and this hands it a resolver so an id on an incoming hit can become a real effect. Same
            // handover shape as SetDamageSystem and for the same reason: AddComponent never goes
            // through VContainer. Null is legal (a bare test spawn) and leaves the resolver-less set,
            // which refuses every id rather than materialising a phantom effect.
            if (_effectResolver != null)
            {
                controller.SetEffectResolver(_effectResolver);
            }

            // ...and the seam that lets its status effects be SEEN. The unit's effects tick and deal
            // their damage without this — only the flame, the drip and the ring are missing, so the
            // failure is silent. Same handover shape as SetEffectResolver and for the same reason:
            // AddComponent never goes through VContainer.
            if (_particleEmitter != null)
            {
                controller.SetParticleEmitter(_particleEmitter);
            }

            // ...and the seam that moves its STEP off Unity's uncapped 50 Hz clock. See
            // UnitController.AttachSimulation for why that matters: a frame that costs more than one
            // fixed step runs every unit again inside it, so the units' own cost becomes the thing
            // that makes the frame long. Null clock/loop is the legacy path, which is what a bare test
            // spawn gets.
            if (_simClock != null && _simLoop != null)
            {
                controller.AttachSimulation(_simClock, _simLoop);
            }

            controller.ApplyPlacement(unit);

            return unitObject;
        }

        /// <summary>
        /// The room's tile query, built once. Null when this spawner was constructed without a room
        /// (which is legal — the tests do it), in which case a unit keeps the collision-callback
        /// fallback in <c>UnitController</c> rather than being forced ungrounded.
        /// </summary>
        ITileQueryService ResolveTileQuery()
        {
            if (_tileQuery == null && _room != null)
            {
                _tileQuery = new UnifiedTileQueryService(_room);
            }

            return _tileQuery;
        }

        /// <summary>
        /// Draw the unit's sprite, or say plainly that there is none.
        ///
        /// <para><b>The art pipeline exists now: <c>UnitSpriteImporter</c></b> (menu
        /// <c>PFE/Art/Import Unit Sprites</c>) slices each unit's sheet into a cell grid and writes the
        /// unit's <b>resting frame</b> — the first cell of its <c>stay</c> state — into
        /// <see cref="UnitDefinition.sprite"/>, which is what this method draws. So a null sprite here
        /// means one of three concrete things, and the warning below says which:</para>
        ///
        /// <list type="number">
        /// <item>the importer has not been run since the sheet data changed;</item>
        /// <item>the unit has no <c>&lt;vis&gt;</c> art at all (the family templates and structural ids —
        /// 44 of them — are not drawable units);</item>
        /// <item>its <c>stay</c> cell fell outside its own sheet, which the importer reports as
        /// <c>OutOfRange</c> rather than clamping onto a different animation's row.</item>
        /// </list>
        ///
        /// <para><b>This method draws the resting frame; <see cref="UnitAnimator"/> takes over from the
        /// next tick.</b> The full per-state animation data lives on
        /// <see cref="UnitDefinition.spriteSheet"/> / <c>animations</c> and was written by the importers
        /// with no reader at all until <c>UnitAnimator</c> landed, which is why a spawned unit used to
        /// stand frozen however much animation its asset carried. The two are layered deliberately: the
        /// resting frame is drawn here <i>first</i>, so a unit with no animation keeps a static sprite
        /// and a unit with animation does not flicker through an empty first frame. Walking, attacking
        /// and dying still need something to call <c>UnitAnimator.SetState</c> — that is the AI port, not
        /// this class.</para>
        ///
        /// <para>Naming the gap rather than rendering nothing silently is the point: an invisible enemy
        /// that is really there is the hardest kind to diagnose. The collider is still built, so the
        /// unit is targetable and damageable while its art is pending.</para>
        ///
        /// <para><b>The sprite is anchored, not just assigned.</b> It goes through
        /// <see cref="UnitSpriteAnchor.ApplyTo"/> rather than a bare <c>renderer.sprite =</c>, because the
        /// imported per-frame PNGs carry Unity's default centre pivot while AS3 draws the frame 10px
        /// below the unit's origin. That is the "units clip under the floor" defect; the type documents
        /// the measurement.</para>
        /// </summary>
        void ApplySprite(SpriteRenderer renderer, UnitInstance unit, UnitDefinition definition)
        {
            Sprite sprite = definition != null ? definition.sprite : null;

            // The registration point comes from the ART, not only from the definition. A turret's
            // resting frame is a DisplayObject MovieClip whose origin is neither the frame's centre nor
            // a 10 px feet gap (UnitTurret.setVisPos is `vis.x = X; vis.y = Y`), and no turret row
            // authors `sprDX`/`sprDY`, so `registrationPoint` is (-1,-1) for all six mounts and the
            // blit rule below drew them 28.5 px left and 56 px high. TurretSpriteOrigin carries the
            // measured origin; every other unit falls through to its own declared value unchanged.
            UnitSpriteAnchor.ApplyTo(
                renderer,
                sprite,
                TurretSpriteOrigin.Resolve(unit.unitId, definition != null ? definition.registrationPoint : new Vector2Int(-1, -1)));
            renderer.enabled = sprite != null;

            // A turret is not one sprite: UnitTurret animates a gun and a lamp inside the mount's own
            // MovieClip (UnitTurret.as:298-313), and the importer flattened that tree into one baked
            // frame. Attach rebuilds the two missing pieces from the baked parts and, when they exist,
            // swaps the body for the gun-free version of the same canvas. Returns null for every unit
            // that is not one of the six mounts, so this is a no-op for the other ~290.
            TurretPartPresenter.Attach(renderer, unit.unitId, renderer.sortingOrder, renderer.sortingLayerName);

            if (sprite == null && _warnedMissingSprite.Add(unit.unitId))
            {
                Debug.LogWarning(
                    $"[RoomUnitSpawner] Unit '{unit.unitId}' has no sprite on its UnitDefinition, so it " +
                    $"is spawned as a collider with no visual. Run PFE/Art/Import Unit Sprites to " +
                    $"populate it; if it stays empty after that, the unit has no <vis> art or its " +
                    $"`stay` cell lies outside its sheet (the importer lists both).");
            }

            Material material = SpritePresenterMaterial.Get();
            if (material != null)
            {
                renderer.sharedMaterial = material;
            }
        }

        /// <summary>
        /// The controller type for a placement's <c>cl</c>. An empty id is normal — units placed by the
        /// random-enemy pass have no authored placement — so only a <i>named but unknown</i> controller
        /// warns, because that is a porting gap rather than the absence of one.
        /// </summary>
        Type ResolveControllerType(string controllerId)
        {
            if (string.IsNullOrWhiteSpace(controllerId))
            {
                return typeof(UnitController);
            }

            if (ControllerTypes.TryGetValue(controllerId, out Type type))
            {
                return type;
            }

            // Fallback: trim trailing digits (e.g. "zombie3" -> "zombie", "dron2" -> "dron", "bloat5" -> "bloat")
            string baseId = System.Text.RegularExpressions.Regex.Replace(controllerId, @"\d+$", "");
            if (!string.IsNullOrWhiteSpace(baseId) && ControllerTypes.TryGetValue(baseId, out Type baseType))
            {
                return baseType;
            }

            if (_warnedUnknownController.Add(controllerId))
            {
                Debug.LogWarning(
                    $"[RoomUnitSpawner] No port for AS3 controller class '{controllerId}' — falling back " +
                    $"to the base UnitController, which has no AI and no per-unit behaviour. " +
                    $"Known: {string.Join(", ", ControllerTypes.Keys)}.");
            }

            return typeof(UnitController);
        }

        void DestroyUnit(UnitInstance unit)
        {
            if (!_spawned.TryGetValue(unit, out GameObject unitObject))
            {
                return;
            }

            _spawned.Remove(unit);
            DestroyObject(unitObject);
        }

        static void DestroyObject(GameObject target)
        {
            if (target == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(target);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }
    }
}
