using System;
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
        public Sprite Sprite;
        public int SortingLayerId;
        public int SortingOrder;
        public Vector2 SpawnPixel;
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
    /// <item><b>Client (<c>Simulated == false</c>):</b> a display-only ghost — a GameObject and a
    /// <c>SpriteRenderer</c>, no physics and no <see cref="UnitController"/>. This is deliberate. A
    /// client-side <c>UnitController</c> would run its own step and integrate its own velocity against
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
        private uint _lastCommandSequence;
        private EntityId _id;

        private PfeRemoteBody(byte slot, EntityId id, Transform root, bool simulated,
            UnitController unit, PlayerLocomotionController locomotion)
        {
            Slot = slot;
            _id = id;
            _root = root;
            _simulated = simulated;
            Unit = unit;
            Locomotion = locomotion;
        }

        public byte Slot { get; }
        public UnitController Unit { get; }
        public PlayerLocomotionController Locomotion { get; }
        public ulong BodyHash => _id.Hash;
        public bool IsSimulated => _simulated;

        public int AppliedCommands { get; private set; }
        public int AppliedStates { get; private set; }
        public int SkippedStaleCommands { get; private set; }
        public int SkippedForeignStates { get; private set; }

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

                UnitController unit = null;
                PlayerLocomotionController locomotion = null;

                if (ctx.Simulated)
                {
                    if (ctx.Definition == null)
                    {
                        error = "no unit definition for a simulated body";
                        return null;
                    }

                    // Motor first is the documented order for an ACTIVE build; the disabled build
                    // makes it irrelevant, and keeping it costs nothing if that ever changes.
                    var motor = go.AddComponent<TilePhysicsController>();
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

                    locomotion = go.AddComponent<PlayerLocomotionController>();
                }

                go.SetActive(true);
                return new PfeRemoteBody(ctx.Slot, id, go.transform, ctx.Simulated, unit, locomotion);
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                if (go != null) UnityEngine.Object.Destroy(go);
                return null;
            }
        }

        /// <summary>Copy the local player's own sprite and sorting settings. A body with a null sprite
        /// renders nothing at all — it exists, ticks and replicates, and is invisible, which is the one
        /// failure a compile and a log both miss.</summary>
        private static void AttachVisual(GameObject go, in PfeRemoteBodyContext ctx)
        {
            if (ctx.Sprite == null) return;

            var visual = new GameObject("Visual");
            visual.SetActive(false);
            visual.transform.SetParent(go.transform, false);

            var renderer = visual.AddComponent<SpriteRenderer>();
            renderer.sprite = ctx.Sprite;
            renderer.sortingLayerID = ctx.SortingLayerId;
            renderer.sortingOrder = ctx.SortingOrder;

            visual.SetActive(true);
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
            AppliedCommands++;
        }

        /// <summary>
        /// Client: move the ghost to the host's authoritative position. Snapped, not interpolated —
        /// smoothing belongs with F-12's interpolation buffer, and a half-written lerp would hide the
        /// real divergence this pass is supposed to expose.
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

            _root.position = WorldCoordinates.PixelToUnity(new Vector2(state.Px, state.Py));
            SetRemoteVisible(true);
            AppliedStates++;
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
    }
}
