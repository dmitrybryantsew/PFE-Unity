using System.Collections.Generic;
using UnityEngine;
using VContainer;
using PFE.Core;
using PFE.Systems.Combat;
using PFE.Data.Definitions;
using PFE.Entities.Weapons;

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

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Initialize with DI dependencies. Called by PlayerWeaponLoadout.Start().</summary>
        public void Initialize(IProjectileFactory factory, IObjectResolver resolver = null, PfeDebugSettings debugSettings = null)
        {
            _factory  = factory;
            _resolver = resolver;
            _debugSettings = debugSettings;
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
                SpawnOne(plan);
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
        }
    }
}
