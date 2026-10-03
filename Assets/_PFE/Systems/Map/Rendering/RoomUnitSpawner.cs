using System;
using System.Collections.Generic;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Map.TileQuery;
using UnityEngine;

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
    public sealed class RoomUnitSpawner
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
                [TrainingDummyController.ControllerId] = typeof(TrainingDummyController),
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
            PFE.Systems.Effects.IEffectDefinitionResolver effectResolver = null)
        {
            _room = room;
            _parent = parent;
            _definitions = definitions ?? ResourcesUnitDefinitionProvider.Shared;
            _damageSystem = damageSystem;
            _simClock = simClock;
            _simLoop = simLoop;
            _effectResolver = effectResolver;
        }

        public int SpawnedCount => _spawned.Count;

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

            Type controllerType = ResolveControllerType(unit.controllerId);
            var controller = (UnitController)unitObject.AddComponent(controllerType);

            // The seam UnitController.Initialize documents as the missing one: without it the unit has
            // no health, no <vulner> table, and logs "no UnitStats assigned" on every hit.
            UnitStats stats = definition != null ? new UnitStats(definition.health, 100f) : null;
            controller.Initialize(definition, stats);

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
            UnitSpriteAnchor.ApplyTo(
                renderer,
                sprite,
                definition != null ? definition.registrationPoint : new Vector2Int(-1, -1));
            renderer.enabled = sprite != null;

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
