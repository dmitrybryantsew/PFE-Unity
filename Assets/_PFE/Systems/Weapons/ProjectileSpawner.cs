using System.Collections.Generic;
using UnityEngine;
using VContainer;
using PFE.Core;
using PFE.Systems.Combat;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Entities.Weapons;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// Reads ShotPlans produced by the active IWeaponController each tick and
    /// spawns the corresponding scene objects.
    ///
    /// Routing:
    ///   Projectile   → IProjectileFactory (pooled Projectile MonoBehaviour)
    ///   ThrownObject → ThrownObjectPrefab (pooled ThrownObject MonoBehaviour)
    ///   Mine         → MinePrefab (pooled MineObject MonoBehaviour)
    ///                  Special case: FuseFrames==0 && IsMine==false → radio detonation signal
    ///   Hitscan      → TODO Stage 3+ (raycast, report a hit to DamageSystem)
    ///   MeleeSweep   → ignored here (handled by MeleeHitVolume trigger)
    ///
    /// Called explicitly by PlayerWeaponLoadout.FixedUpdate — not via Unity messages.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ProjectileSpawner : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("Thrown / Mine Prefabs")]
        [SerializeField]
        [Tooltip("Prefab with ThrownObject component. Required for throwTip==0/2 weapons.")]
        private ThrownObject _thrownObjectPrefab;

        [SerializeField]
        [Tooltip("Prefab with MineObject component. Required for throwTip==1 weapons.")]
        private MineObject _minePrefab;

        // ── Dependencies ──────────────────────────────────────────────────────

        private IProjectileFactory _factory;
        private IObjectResolver    _resolver;
        private WeaponDefinition   _currentDef;
        private PfeDebugSettings   _debugSettings;

        /// <summary>
        /// The land, for the mine placement retry — <c>WThrow.as:163-172</c> needs the room's tile
        /// grid, which is reached through the live <c>currentRoom</c> rather than captured once (a
        /// door moves the player between rooms without re-running the wiring).
        ///
        /// <para>Resolved from the container in <see cref="Initialize"/>. Null is legal — a test or a
        /// scene with no map — and then a placed mine simply keeps the "no room" reduced answer: no
        /// retry, no pin, and <see cref="MineObject"/> never falls.</para>
        /// </summary>
        private LandMap _landMap;

        /// <summary>Cache for <see cref="ResolveTileQuery"/>, keyed on the room it was built for.</summary>
        private ITileQueryService _tileQuery;

        /// <summary>
        /// The room's particle emitter, used to draw a shot's muzzle flare.
        ///
        /// <para><b>Why this is resolved here rather than injected.</b> The same reason
        /// <see cref="_landMap"/> is: the spawner is constructed with <c>new</c> by
        /// <c>PlayerWeaponLoadout</c>, which is a scene component, so nothing runs
        /// <c>[Inject]</c> on it. Null is legal — a bare test, or a scene with no particle world — and
        /// then a shot simply has no flare, which is the state every weapon in the game was in before
        /// this was wired.</para>
        /// </summary>
        private PFE.Systems.Particles.Adapters.RoomParticleEmitter _particleEmitter;

        /// <summary>Cache for <see cref="ResolveOwnerUnit"/>, invalidated when <see cref="Owner"/> changes.</summary>
        private UnitController _ownerUnit;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// AS3's <c>owner</c> — the unit that fired, whose position the mine placement retry falls
        /// back to (<c>setPos(owner.X, owner.Y)</c>, <c>WThrow.as:165</c>). Set by
        /// <see cref="PlayerWeaponLoadout"/>; null for a spawner with no owner, which skips the retry
        /// and leaves the mine to be pinned instead.
        /// </summary>
        public Transform Owner { get; set; }

        /// <summary>Initialize with DI dependencies. Called by PlayerWeaponLoadout.Start().</summary>
        public void Initialize(IProjectileFactory factory, IObjectResolver resolver = null, PfeDebugSettings debugSettings = null)
        {
            _factory  = factory;
            _resolver = resolver;
            _debugSettings = debugSettings;

            // Only for the mine placement retry. A resolver that cannot supply the land (a bare test
            // container) must not take the spawner down with it — the mine path degrades to "no room",
            // which is a documented state rather than a crash.
            if (_resolver != null && _landMap == null)
            {
                try { _landMap = _resolver.Resolve<LandMap>(); }
                catch { _landMap = null; }
            }

            // ...and the muzzle-flare emitter, on the same terms: optional, resolved rather than
            // injected, and null is a legitimate "no flare" state rather than a wiring failure.
            if (_resolver != null && _particleEmitter == null)
            {
                try { _particleEmitter = _resolver.Resolve<PFE.Systems.Particles.Adapters.RoomParticleEmitter>(); }
                catch { _particleEmitter = null; }
            }
        }

        /// <summary>Called by PlayerWeaponLoadout after Equip().</summary>
        public void SetWeapon(WeaponDefinition def)
        {
            _currentDef = def;
            if (_debugSettings?.LogProjectileSpawning == true && def != null)
                Debug.Log($"[ProjectileSpawner] SetWeapon('{def.weaponId}') archetype={def.projectileArchetype} visual='{def.projectileVisual?.name ?? "null"}'.");
        }

        /// <summary>
        /// Spawn objects for all plans. Called from PlayerWeaponLoadout.FixedUpdate.
        /// </summary>
        public void SpawnFromPlans(IReadOnlyList<ShotPlan> plans)
        {
            if (_currentDef == null)
            {
                if (_debugSettings?.LogProjectileSpawning == true)
                    Debug.LogWarning("[ProjectileSpawner] SpawnFromPlans called with no current weapon.");
                return;
            }

            if (plans.Count == 0) return;

            if (_debugSettings?.LogProjectileSpawning == true)
                Debug.Log($"[ProjectileSpawner] SpawnFromPlans weapon='{_currentDef.weaponId}' count={plans.Count}.");

            foreach (var plan in plans)
            {
                AnnounceShot(plan);
                SpawnMuzzleFlare(plan);
                SpawnOne(plan);
            }
        }

        /// <summary>
        /// Draw the shot's muzzle flare — AS3 <c>vis.@flare</c>, emitted at the bullet's spawn point.
        ///
        /// <para><b><see cref="ShotCues.SpawnMuzzleFlash"/> was written by all five weapon controllers
        /// and read by nobody</b>, so no weapon in the game — the player's included — drew a flare even
        /// though every definition carries a <c>muzzleFlareId</c> and the emitter table has the id
        /// (<c>alilight</c>'s <c>flare='spark'</c> resolves to the <c>spark</c> particle →
        /// <c>flSpark</c>). This is that cue's reader.</para>
        ///
        /// <para>The id comes from the <b>definition</b>, not the plan, for the same reason
        /// <see cref="SpawnProjectile"/> reads the definition: the plan carries no flare field, and
        /// adding one would be a second copy of a value the spawner already owns.</para>
        /// </summary>
        private void SpawnMuzzleFlare(ShotPlan plan)
        {
            if (!plan.Cues.SpawnMuzzleFlash) return;
            if (_particleEmitter == null) return;
            if (_currentDef == null || string.IsNullOrEmpty(_currentDef.muzzleFlareId)) return;

            _particleEmitter.Emit(
                _currentDef.muzzleFlareId,
                new Vector3(plan.WorldPosition.x, plan.WorldPosition.y, 0f));
        }

        /// <summary>
        /// Spend the shot's noise — AS3 <c>Unit.makeNoise(weap.noise, true)</c>, reached from
        /// <c>Unit.crash(bullet)</c> when a round goes off and from the melee/throw paths directly.
        ///
        /// <para><b>This is the loudest thing in the game and it was being computed and thrown
        /// away.</b> Every weapon controller already fills <see cref="ShotCues.MakeNoise"/> and
        /// <see cref="ShotCues.NoiseRadius"/> from the weapon's <c>&lt;snd noise='…'/&gt;</c> — 400 for a
        /// punch, 700 for a 10 mm, up to 1300 for a minigun (<c>actionscript_project_context.txt:3713</c>)
        /// — and <c>ShotCues</c>'s fields had no reader anywhere. In the oracle a punch (400) carries
        /// twice as far as a sprint (200), which is the entire shape of the stealth design: you can run
        /// past a guard but you cannot shoot past one.</para>
        ///
        /// <para><b>The plan's value is used, not the definition's</b>, for the reason the projectile
        /// path already documents for penetration: a debug-swapped or ammo-fed round has to be as loud
        /// as the round that actually left the barrel, and the definition is shared by every wielder of
        /// that weapon. <c>ShotCues.NoiseRadius</c> is carried in Unity units — every controller divides
        /// the AS3 px value by <c>PpuScale</c> = 100 — so it is multiplied back here, the one place that
        /// needs it in the oracle's own units. Dividing on the way in and multiplying on the way out is
        /// deliberate rather than a round trip to nowhere: the plan is consumed by Unity-space code
        /// (the projectile, the presenter), and only the acoustic model speaks pixels.</para>
        ///
        /// <para><c>isEvent: true</c> because a shot is a discrete event: two rounds in quick succession
        /// should draw two ripples, where footsteps draw one per cooldown.</para>
        /// </summary>
        private void AnnounceShot(ShotPlan plan)
        {
            if (!plan.Cues.MakeNoise || plan.Cues.NoiseRadius <= 0f)
            {
                return;
            }

            UnitController shooter = ResolveOwnerUnit();
            if (shooter == null)
            {
                return;
            }

            int noisePixels = Mathf.RoundToInt(plan.Cues.NoiseRadius * TileQueryConstants.UnitToPixel);
            if (noisePixels > 0)
            {
                shooter.MakeNoise(noisePixels, isEvent: true);
            }
        }

        /// <summary>
        /// The firing unit — <see cref="Owner"/> resolved to a <see cref="UnitController"/>, cached
        /// because this runs once per shot and <c>GetComponent</c> per shot per round is exactly the
        /// kind of cost a minigun makes visible.
        /// </summary>
        private UnitController ResolveOwnerUnit()
        {
            if (_ownerUnit != null && Owner != null && _ownerUnit.transform == Owner)
            {
                return _ownerUnit;
            }

            _ownerUnit = Owner != null ? Owner.GetComponentInParent<UnitController>() : null;
            return _ownerUnit;
        }

        // ── Internal ──────────────────────────────────────────────────────────

        private void SpawnOne(ShotPlan plan)
        {
            switch (plan.Kind)
            {
                case ShotKind.Projectile:
                    SpawnProjectile(plan);
                    break;

                case ShotKind.ThrownObject:
                    SpawnThrownObject(plan);
                    break;

                case ShotKind.Mine:
                    SpawnMineOrDetonate(plan);
                    break;

                case ShotKind.Hitscan:
                    // TODO Stage 3+: cast ray, report the hit to DamageSystem.
                    Debug.Log("[ProjectileSpawner] Hitscan not yet implemented.");
                    break;

                case ShotKind.MeleeSweep:
                    // MeleeHitVolume handles its own trigger — nothing to spawn here.
                    break;
            }
        }

        private void SpawnProjectile(ShotPlan plan)
        {
            if (_factory == null)
            {
                Debug.LogWarning("[ProjectileSpawner] SpawnProjectile aborted because IProjectileFactory is null.");
                return;
            }

            Vector2 dir      = new Vector2(Mathf.Cos(plan.AngleRad), Mathf.Sin(plan.AngleRad));
            Vector3 spawnPos = new Vector3(plan.WorldPosition.x, plan.WorldPosition.y, 0f);

            if (_debugSettings?.LogProjectileSpawning == true)
            {
                // Log the raw AS3 speed (px/frame) rather than a converted value: the one
                // conversion lives in ProjectileFactory, and repeating it here is how the
                // `* fps/PPU` vs `/PPU` idiom got mixed up in the first place.
                Debug.Log(
                    $"[ProjectileSpawner] Spawning projectile weapon='{_currentDef.weaponId}' " +
                    $"origin={plan.Origin} kind={plan.Kind} pos={spawnPos} dir={dir} " +
                    $"speedPxPerFrame={_currentDef.projectileSpeed:0.###}.");
            }

            // Pass the shot's own penetration budget (weapon probiv + ammo probiv, clamped) rather
            // than letting the factory read the definition's: a debug-swapped or ammo-fed round has to
            // penetrate at the value the shot actually carries, and the definition is shared by every
            // wielder of that weapon. See DamageContext.PenetrationChance.
            Projectile proj = _factory.Create(_currentDef, spawnPos, dir,
                penetrationOverride: plan.Damage.PenetrationChance);
            if (proj == null)
            {
                if (_debugSettings?.LogProjectileSpawning == true)
                    Debug.LogWarning($"[ProjectileSpawner] Factory returned null for weapon '{_currentDef.weaponId}'.");
                return;
            }

            proj.SetDamageContext(plan.Damage);

            // `vis.@spring`, for the stretched-beam view — read off the definition here for the same
            // reason the rest of the shot is (see ShotPlan's "Where projectile physics went"): the
            // spawner owns the definition, and a per-shot copy would be a second place to get it wrong.
            proj.SetSpringMode(_currentDef.springMode);
        }

        private void SpawnThrownObject(ShotPlan plan)
        {
            if (_thrownObjectPrefab == null)
            {
                Debug.LogWarning("[ProjectileSpawner] ThrownObject prefab not assigned.");
                return;
            }

            Vector3 spawnPos = new Vector3(plan.WorldPosition.x, plan.WorldPosition.y, 0f);
            ThrownObject obj = Instantiate(_thrownObjectPrefab, spawnPos, Quaternion.identity);

            _resolver?.Inject(obj);

            obj.Initialize(
                initialVelocity: plan.ThrowVelocity,
                fuseFrames:      plan.FuseFrames,
                explRadius:      _currentDef.explRadius / 100f,
                // `bumc`, i.e. <phis bumc> — NOT isPhysBullet. Those are different attributes on
                // different nodes (vis@phisbul selects the archetype; phis@bumc decides contact
                // detonation) and their weapon sets do not overlap, so reading the wrong one meant
                // acidgr and molotov never detonated on contact.
                bumc:            _currentDef.bumc,
                // AS3's thrown-object constants come from WThrow, not from the bullet class's own
                // defaults. Passed explicitly rather than left to Initialize's defaults so the
                // source is visible at the call site.
                skok:            ProjectilePhysicsMath.ThrowBounceRetention,
                tormoz:          ProjectilePhysicsMath.ThrowFloorDamping,
                brake:           ProjectilePhysicsMath.BrakePxPerFrame2,
                // `lip` — throwTip == 2 latches on the first tile contact instead of bouncing.
                sticky:          plan.Sticky,
                // The blast's presentation — the same two inputs ProjectileFactory passes. `visexpl` is
                // the per-weapon override `Bullet.explVis()` reads first (Weapon.as:623-625); the damage
                // type selects the fallback arm of the table. Omitting them made every throwable detonate
                // silently: the damage landed, the visual and the sound did not.
                damageType:      _currentDef.damageType,
                visExpl:         _currentDef.visExpl,
                // AS3 `WThrow.as:196 (b as PhisBullet).sndHit = this.sndFall` — the weapon's
                // `<snd fall>`, played on every tile contact. Omitting it made every grenade land
                // silently; the mines carry the same attribute (`fall='fall_metal_small'`).
                soundFall:       _currentDef.soundFall,
                // The blast's SHAPE and PULSE COUNT — `char.@expltip` / `char.@explkol`, copied to the
                // bullet by `Weapon.setBullet` (`Weapon.as:1676/:1678`). `explKol` above 1 is the
                // sustained damage area: fgren/molotov (10), gasgr/acidgr (12). Omitting both left every
                // blast a single pulse of the default shape, which is the owner-reported "no damage
                // area".
                explTip:         _currentDef.explTip,
                explKol:         _currentDef.explKol);

            obj.SetDamageContext(plan.Damage);

            // The weapon's OWN art, not `projectileVisual`. AS3's WThrow overwrites the bullet class
            // with the weapon class (`WThrow.as:38 vBullet = vWeapon`), so a thrown grenade is drawn
            // with `vis<weaponId>` — the same symbol the weapon has in hand. Every throwable's
            // <vis> block is `tipdec`/`icomult` only, with no `vbul`, so `projectileVisual` is the
            // wrong family for all of them.
            obj.ApplyVisual(_currentDef.weaponVisual);
        }

        private void SpawnMineOrDetonate(ShotPlan plan)
        {
            // Radio detonation signal: FuseFrames==0 && IsMine==false.
            if (plan.FuseFrames == 0 && !plan.IsMine)
            {
                // AS3: detonator() calls activate() on all matching mines.
                MineObject.DetonateAll(_currentDef.weaponId);
                return;
            }

            // New mine placement.
            if (_minePrefab == null)
            {
                Debug.LogWarning("[ProjectileSpawner] Mine prefab not assigned.");
                return;
            }

            Vector3 spawnPos = new Vector3(plan.WorldPosition.x, plan.WorldPosition.y, 0f);
            MineObject mine  = Instantiate(_minePrefab, spawnPos, Quaternion.identity);

            _resolver?.Inject(mine);

            mine.Initialize(
                weaponId:     _currentDef.weaponId,
                explRadius:   _currentDef.explRadius / 100f,
                fuseFrames:   plan.FuseFrames,
                armingFrames: 75,                 // AS3 WThrow.as:157 — hardcoded, not data
                sensPx:       _currentDef.sens,   // AS3 Mine.sens; 0 = radio-only (x37)
                maxHp:        _currentDef.maxDurability,
                // The blast's presentation. AS3 `Unit.explosion()` sets `weapId = this.id` on a throwaway
                // Bullet, so the mine's blast reads the PLACING weapon's `visexpl` and damage type — the
                // same pair the projectile and thrown-object paths carry.
                damageType:   _currentDef.damageType,
                visExpl:      _currentDef.visExpl,
                // AS3 `Mine.as:118 this.sndSens = node.snd.@sens` — the arming beep, played every
                // 5 ticks while counting down (`:340-343`). Only the eight mine rows carry it.
                soundSens:    _currentDef.soundSens);

            mine.SetDamageContext(plan.Damage);

            // `Mine.as:98` resolves the view as `Res.getVis("vis" + id, vismine)` — the placing
            // weapon's own symbol again, so the mine is drawn with the same art family as the
            // throwable that placed it.
            mine.ApplyVisual(_currentDef.weaponVisual);

            // The mine is a Unit in AS3, so it needs the room's tile grid: to fall (`isLaz`,
            // Unit.as:1962) and to answer the placement retry's `collisionAll()` (Unit.as:2541).
            mine.SetTileQuery(ResolveTileQuery());

            ResolveMinePlacement(mine);
        }

        /// <summary>
        /// AS3 <c>WThrow.as:163-172</c> — the placement retry and the pin, in the oracle's order:
        ///
        /// <code>
        /// if(_loc5_.collisionAll()) { _loc5_.setPos(owner.X, owner.Y); }   // retry at the owner's feet
        /// if(_loc5_.collisionAll()) { _loc5_.fixed = true; }               // still stuck ⇒ freeze it
        /// </code>
        ///
        /// <para><b>Both tests re-run against the CURRENT position</b>, which is why this cannot be
        /// collapsed into one branch: the second <c>collisionAll()</c> asks about the position the
        /// first one just moved the mine to.</para>
        ///
        /// <para>The retry is skipped when there is no owner — AS3 would dereference null there, so
        /// the port chooses the recoverable half: leave the mine where it is and let the pin catch
        /// it, rather than dropping a mine that can never be placed.</para>
        /// </summary>
        private void ResolveMinePlacement(MineObject mine)
        {
            bool collidesAtHand = mine.CollidesWithTiles();
            if (!collidesAtHand) return;                       // legal at the weapon's position

            bool canRetry = Owner != null;
            if (canRetry)
            {
                mine.PlaceAt(Owner.position);                  // AS3 `setPos(owner.X, owner.Y)`
            }

            bool collidesAfterRetry = canRetry && mine.CollidesWithTiles();

            // The decision itself is a pure rule (`MineBodyMath`) so the two-question ordering is
            // asserted offline rather than only here.
            if (MineBodyMath.ResolvePlacement(collidesAtHand, canRetry, collidesAfterRetry)
                == MinePlacement.Pin)
            {
                mine.PinInPlace();
            }
        }

        /// <summary>
        /// The room's tile query, rebuilt when the room changes. Mirrors
        /// <c>RoomUnitSpawner.ResolveTileQuery</c> — same service, same construction, same reason
        /// (the seam is created on demand rather than registered, because it is room-scoped).
        /// </summary>
        private ITileQueryService ResolveTileQuery()
        {
            RoomInstance room = _landMap != null ? _landMap.currentRoom : null;
            if (room == null) return null;

            if (_tileQuery != null && ReferenceEquals(_tileQuery.Room, room)) return _tileQuery;

            _tileQuery = new UnifiedTileQueryService(room);
            return _tileQuery;
        }
    }
}
