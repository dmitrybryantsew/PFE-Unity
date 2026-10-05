using System;
using PFE.Character;
using PFE.Character.Animation;
using PFE.Core;
using PFE.Core.Rng;
using PFE.Data;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Entities.Weapons;
using PFE.Systems.Audio;
using PFE.Systems.Combat;
using PFE.Systems.Effects;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Particles.Adapters;
using PFE.Systems.Physics;
using PFE.Systems.Weapons;
using UnityEngine;
using VContainer;

namespace PFE.Entities.Player.Rig
{
    /// <summary>
    /// Everything a code-built player cannot resolve for itself.
    ///
    /// <para>Every field here exists because of one fact: <b>VContainer never observes
    /// <c>AddComponent</c></b>, so a <c>[Inject]</c> on the player is dead unless the value is
    /// handed over. The service list mirrors <c>RoomUnitSpawner</c>'s seven seams (that class is the
    /// repo's existing dynamic entity factory) plus the player's own
    /// <see cref="PlayerWeaponLoadout.Construct"/>.</para>
    ///
    /// <para><b>Null is legal for most of these</b>, exactly as it is in the spawner: a bare probe
    /// rig with no container is a normal state, and the components degrade in documented ways rather
    /// than throwing. <see cref="TileQuery"/> is the one that genuinely matters — without it the
    /// player cannot answer "is there floor under me" and falls through the world.</para>
    /// </summary>
    public sealed class PlayerRigContext
    {
        // ── Where to build ────────────────────────────────────────────────────
        public Vector3 SpawnPosition;
        public Transform Parent;

        // ── UnitController's seams ────────────────────────────────────────────
        public UnitDefinition UnitDefinition;
        public UnitStats UnitStats;
        public ITileQueryService TileQuery;
        public RoomObjectPhysicsLayer ObjectPhysicsLayer;
        public DamageSystem DamageSystem;
        public IEffectDefinitionResolver EffectResolver;
        public RoomParticleEmitter ParticleEmitter;
        public SimClock SimClock;
        public SimLoop SimLoop;

        // ── PlayerWeaponLoadout.Construct ─────────────────────────────────────
        public IProjectileFactory ProjectileFactory;
        public IObjectResolver Resolver;
        public PfeDebugSettings DebugSettings;
        public ISoundService SoundService;
        public IRngService Rng;
        public ContentRegistry Registry;

        // ── Appearance (already data-driven — needs no private access) ────────
        public CharacterAnimationDefinition AnimationDefinition;
        public CharacterStyleData StyleData;
        public CharacterAppearance Appearance;

        /// <summary>
        /// The sorting layer the avatar draws on. <b>Not cosmetic.</b> The assembler's own field
        /// defaults to <c>"Default"</c>, which draws the character BEHIND the level graphics — the
        /// rig exists, animates, and is invisible, with nothing logged. The prefab uses
        /// <c>"Foreground"</c>.
        /// </summary>
        public string SortingLayerName;
    }

    /// <summary>
    /// Builds the player from code instead of instantiating <c>Player.prefab</c>.
    ///
    /// <para><b>Why this is not <c>new GameObject()</c> + <c>AddComponent</c>.</b> An entity here is
    /// three separate contracts. (A) <b>VContainer</b>: <c>GameLifetimeScope</c> registers the player
    /// with <c>FindFirstObjectByType</c> and the scene lists it in <c>autoInjectGameObjects</c>, both
    /// during scope <i>configuration</i> — before this can run — and <c>AddComponent</c> is never
    /// observed, so every <c>[Inject]</c> must be handed over (see <see cref="PlayerRigContext"/>).
    /// (B) <b>Hand-over</b>: the same seven seams <c>RoomUnitSpawner.Spawn</c> passes to a spawned
    /// unit. (C) <b>Tuning</b>: the player's ~50 tunables are <c>[SerializeField] private</c>, so a
    /// bare <c>AddComponent</c> yields C# field-initialiser defaults, not the prefab's authored
    /// values — see <see cref="PlayerRigTuning"/>.</para>
    ///
    /// <para><b>Build INACTIVE, then activate.</b> This is the load-bearing detail, and it is why this
    /// builder does <i>not</i> hand-order its <c>AddComponent</c> calls the way
    /// <c>RoomUnitSpawner.Spawn</c> has to. In a prefab every component already exists before any
    /// <c>Awake</c> runs, so each <c>Awake</c>'s <c>GetComponent</c> lookups succeed whatever the
    /// component order. On a <i>live</i> GameObject, <c>AddComponent</c> fires <c>Awake</c>
    /// synchronously, so adding <c>PlayerLocomotionController</c> before <c>CharacterAnimationDriver</c>
    /// silently leaves the driver's <c>_locomotion</c> null forever — there is no retry. Building the
    /// object disabled and activating it at the end restores the prefab's semantics: every component
    /// exists, then every <c>Awake</c> runs together.</para>
    /// </summary>
    public static class PlayerRigBuilder
    {
        public const string RigName = "Player_RigCode";

        /// <summary>
        /// Builds a player rig. <paramref name="tuning"/> is applied <b>after</b> activation, because
        /// <c>Awake</c> has already run by then and some <c>Awake</c>s derive values from their field
        /// initialisers (for example <c>PlayerLocomotionAbilities</c> back-fills
        /// <c>levitationMaxHeight</c> when it is <c>&lt;= 0</c>). Applying afterwards overwrites those
        /// derivations with the authored values, which is what restores parity. A null
        /// <paramref name="tuning"/> is legal and leaves the C# defaults in place — the diff will then
        /// report exactly which ones differ, which is the point of the probe.
        /// </summary>
        public static GameObject Build(PlayerRigContext ctx, PlayerRigSnapshot tuning = null)
        {
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));

            var root = new GameObject(RigName);

            // Disabled BEFORE the first AddComponent, so no Awake runs until the object is complete.
            root.SetActive(false);

            if (ctx.Parent != null)
            {
                root.transform.SetParent(ctx.Parent, false);
            }

            root.transform.position = ctx.SpawnPosition;
            root.tag = "Player";

            // ── Bodies. UnitController.Awake reads both, and on activation they will already be
            //    here. Kinematic because every mover in this project drives the body by hand.
            var body = root.AddComponent<Rigidbody2D>();
            body.bodyType = RigidbodyType2D.Kinematic;
            var box = root.AddComponent<BoxCollider2D>();

            // ── Children. PlayerWeaponLoadout.Awake resolves these by TYPE with
            //    GetComponentInChildren, so the names are cosmetic but the types are not.
            var visual = NewChild(root.transform, "vis");
            var visualRenderer = visual.AddComponent<SpriteRenderer>();

            NewChild(root.transform, "WHoldPoint");

            var weapon = NewChild(root.transform, "Weapon");
            weapon.AddComponent<WeaponPresenter>();
            NewChild(weapon.transform, "muzzle");

            var hitVolume = NewChild(root.transform, "WeaponHitVolume");
            hitVolume.AddComponent<CapsuleCollider2D>();
            hitVolume.AddComponent<MeleeHitVolume>();

            // ── Components. Order is free here (nothing has Awoken yet); it is written in the
            //    prefab's own order so a future reader can diff the two lists by eye.
            root.AddComponent<CharacterSpriteAssembler>();
            root.AddComponent<TilePhysicsController>();
            root.AddComponent<PlayerLocomotionController>();
            root.AddComponent<PlayerLocomotionAbilities>();
            root.AddComponent<PlayerCharacterVisual>();
            root.AddComponent<CharacterAnimationDriver>();
            root.AddComponent<CharacterAudioEventHandler>();
            root.AddComponent<WeaponMounts>();
            root.AddComponent<ProjectileSpawner>();
            root.AddComponent<PlayerWeaponLoadout>();
            root.AddComponent<PlayerActionInteractor>();
            root.AddComponent<PlayerController>();

            // ── Pre-activation configuration. Everything that an Awake READS must be in place now,
            //    because activation is the point at which those Awakes run.
            //
            //    PlayerCharacterVisual keeps its OWN copies of the animation assets, and its Awake
            //    logs an error and RETURNS when either is null — silently disabling the whole visual
            //    (no Setup, no armour binding). It is not enough to configure the assembler beside it.
            var visualComponent = root.GetComponent<PlayerCharacterVisual>();
            visualComponent._definition = ctx.AnimationDefinition;
            visualComponent._styleData = ctx.StyleData;

            //    Sorting is applied by the assembler to every slot renderer it creates, from a field
            //    that defaults to "Default" — i.e. behind the level. Set it before activation so the
            //    Awake's Setup builds the slots on the right layer in the first place.
            if (!string.IsNullOrEmpty(ctx.SortingLayerName))
            {
                root.GetComponent<CharacterSpriteAssembler>().SortingLayerName = ctx.SortingLayerName;
                visualRenderer.sortingLayerName = ctx.SortingLayerName;
            }

            // ── The point at which every Awake fires, together, as they would from a prefab.
            root.SetActive(true);

            // ── Seam B: hand over what AddComponent cannot inject. These are ordinary method calls,
            //    so they land after Awake and before the first Start/Update — the same relative
            //    position RoomUnitSpawner gives them.
            var controller = root.GetComponent<PlayerController>();
            controller.Initialize(ctx.UnitDefinition, ctx.UnitStats);

            if (ctx.TileQuery != null) controller.SetTileQuery(ctx.TileQuery);
            if (ctx.ObjectPhysicsLayer != null) controller.SetObjectPhysicsLayer(ctx.ObjectPhysicsLayer);
            if (ctx.DamageSystem != null) controller.SetDamageSystem(ctx.DamageSystem);
            if (ctx.EffectResolver != null) controller.SetEffectResolver(ctx.EffectResolver);
            if (ctx.ParticleEmitter != null) controller.SetParticleEmitter(ctx.ParticleEmitter);
            if (ctx.SimClock != null && ctx.SimLoop != null) controller.AttachSimulation(ctx.SimClock, ctx.SimLoop);

            // The loadout's own VContainer seam. Its Start() then initialises the ProjectileSpawner
            // from the factory this hands it, so this must land before Start — which it does, since
            // Start runs no earlier than the next frame after activation.
            var loadout = root.GetComponent<PlayerWeaponLoadout>();
            loadout.Construct(ctx.ProjectileFactory, ctx.Resolver, ctx.DebugSettings,
                              ctx.SoundService, ctx.Rng, ctx.Registry);

            // ── Mount points. Left unassigned the loadout's hold point silently falls back to the
            //    player's own transform, which is wrong rather than merely unauthored — the
            //    Has*Point flags exist precisely to tell those two apart.
            var mounts = root.GetComponent<WeaponMounts>();
            var holdPoint = root.transform.Find("WHoldPoint");
            mounts._weaponHoldPoint = holdPoint;
            mounts._magicHoldPoint = holdPoint;   // non-unicorn fallback, per WeaponMounts' own docs
            mounts._throwPoint = holdPoint;

            // ── Appearance. PlayerCharacterVisual.Awake already ran and set up a default appearance
            //    from GameBootData; re-running Setup is the public, supported way to override it.
            if (ctx.AnimationDefinition != null)
            {
                root.GetComponent<CharacterSpriteAssembler>()
                    .Setup(ctx.AnimationDefinition, ctx.StyleData,
                           ctx.Appearance ?? CharacterAppearance.CreateDefault());
            }

            // ── Seam C: the authored tunables. Last, so it wins over every Awake derivation.
            if (tuning != null)
            {
                PlayerRigTuning.Apply(root, tuning);
            }

            // ── Size the collision box from whatever the motor ended up with — after Apply, so a
            //    tuned rig gets a tuned box. AS3 Unit.as:1875-1876 puts the box ENTIRELY above the
            //    origin, because the origin is the unit's feet; a box centred on the origin buries
            //    half of itself in the floor tile the unit is standing on.
            var tile = root.GetComponent<TilePhysicsController>();
            float boxHeight = tile.collisionHeight;
            box.size = new Vector2(tile.collisionWidth, boxHeight);
            box.offset = new Vector2(0f, boxHeight * 0.5f);

            return root;
        }

        static GameObject NewChild(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go;
        }
    }
}
