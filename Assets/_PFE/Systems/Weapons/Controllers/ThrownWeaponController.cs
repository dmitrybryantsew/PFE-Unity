using System.Collections.Generic;
using UnityEngine;
using PFE.Data.Definitions;

namespace PFE.Systems.Weapons.Controllers
{
    /// <summary>
    /// Full AS3-parity implementation of WThrow.as for tip==4 thrown weapons.
    ///
    /// Two sub-behaviors driven by WeaponDefinition.throwTip:
    ///   0 / 2 — arc throw: compute initial velocity from cursor distance + weaponSkill,
    ///           emit ShotPlan { Kind = ThrownObject } with ThrowVelocity baked in.
    ///   1     — mine placement: emit ShotPlan { Kind = Mine } at current position,
    ///           ThrownObject sits stationary with reloadTime arming delay.
    ///
    /// Radio detonation (WeaponDefinition.radio):
    ///   On BeginAttack() while TAttack == 0, if radio is set the controller emits
    ///   a detonation ShotPlan (Kind = Mine, FuseFrames = 0, IsMine = false) which
    ///   ProjectileSpawner forwards to MineObject.DetonateAll(weaponId).
    ///
    /// Ammo: simplified — uses internal kolAmmo magazine (no inventory system yet).
    ///
    /// AS3 source: WThrow.as (attack/shoot/animate/getVel/getRot)
    /// </summary>
    public sealed class ThrownWeaponController : IWeaponController
    {
        // ── Constants ─────────────────────────────────────────────────────────

        private const float FlashFps = 30f;
        private const float PpuScale = 100f;

        // ── State ─────────────────────────────────────────────────────────────

        public WeaponRuntimeState State { get; }
        private readonly WeaponDefinition _def;

        private float   _frameAccum;
        private bool    _attackHeld;
        private bool    _attackJustPressed;
        private Vector2 _lastAimTarget;

        // Simplified ammo counter (AS3 kolAmmo, default 4).
        // Will be replaced by inventory integration in a future stage.
        private int _kolAmmo;
        private const int DefaultKolAmmo = 4;

        // weaponSkill multiplier — will come from CharacterStats later.
        private const float DefaultWeaponSkill = 1f;

        private readonly List<ShotPlan> _plans = new();

        // ── Constructor ───────────────────────────────────────────────────────

        public ThrownWeaponController(WeaponRuntimeState state)
        {
            State    = state;
            _def     = state.Def;
            _kolAmmo = _def.magazineSize > 0 ? _def.magazineSize : DefaultKolAmmo;
        }

        // ── IWeaponController ─────────────────────────────────────────────────

        public void BeginAttack()
        {
            _attackHeld        = true;
            _attackJustPressed = true;
        }

        public void EndAttack() => _attackHeld = false;
        public void StartReload() { }

        public void Tick(float dt, Vector2 holdPoint, Vector2 hornPoint, Vector2 aimTarget)
        {
            _frameAccum += dt * FlashFps;
            int frames = Mathf.FloorToInt(_frameAccum);
            _frameAccum -= frames;

            for (int i = 0; i < frames; i++)
                TickOneFlashFrame(holdPoint, aimTarget);

            _attackJustPressed = false;
            State.SyncReactive();
        }

        public IReadOnlyList<ShotPlan> FlushShotPlans()
        {
            if (_plans.Count == 0) return System.Array.Empty<ShotPlan>();
            var result = new List<ShotPlan>(_plans);
            _plans.Clear();
            return result;
        }

        public void Dispose() => State.Dispose();

        // ── Per-flash-frame ────────────────────────────────────────────────────

        private void TickOneFlashFrame(Vector2 holdPoint, Vector2 aimTarget)
        {
            _lastAimTarget = aimTarget;

            // Position lerp (same as ranged — weapon sprite tracks hold point).
            if (State.X == 0f && State.Y == 0f)
            {
                State.X = holdPoint.x;
                State.Y = holdPoint.y;
            }
            else
            {
                State.X += (holdPoint.x - State.X) / 5f;
                State.Y += (holdPoint.y - State.Y) / 5f;
            }

            State.Rot   = Mathf.Atan2(aimTarget.y - State.Y, aimTarget.x - State.X);
            State.Ready = true;

            if (_attackHeld)
                RunAttack();

            RunActions();

            State.IsShoot   = false;
            State.WasAttack = State.IsAttack;
            State.IsAttack  = false;
        }

        /// <summary>
        /// Mirrors WThrow.attack().
        /// </summary>
        private void RunAttack()
        {
            if (State.TAttack > 0) return;

            State.IsAttack = true;

            // Single-shot debounce (non-auto thrown weapons).
            bool isAuto = _def.rapid <= 6;
            if (!isAuto && State.TAuto > 0)
            {
                State.TAuto = 3;
                return;
            }

            // Radio detonation: on first press while mines exist, detonate instead of throw.
            if (_def.radio && _attackJustPressed)
            {
                EmitDetonation();
                return;
            }

            if (!ConsumeAmmo()) return;

            State.TAttack = Mathf.RoundToInt(_def.rapid);
        }

        private void RunActions()
        {
            // Shoot trigger at t_attack == rapid (single pulse, not burst).
            if (State.TAttack > 0 && State.TAttack == Mathf.RoundToInt(_def.rapid))
                Shoot();

            if (State.TAttack > 0) State.TAttack--;
            if (State.TRet    > 0) State.TRet--;
            if (State.TShoot  > 0) State.TShoot--;
            if (State.TAuto   > 0) State.TAuto--;
            else                    State.Pow = 0;

            if (State.TPrep > 0) State.TPrep--;
            else                  State.KolShoot = 0;
        }

        /// <summary>
        /// Mirrors WThrow.shoot().
        /// Mine placement (throwTip==1) or arc throw (throwTip==0/2).
        /// </summary>
        private void Shoot()
        {
            DamageContext damCtx = DamageContext.FromWeapon(_def, null);

            ShotCues cues = new ShotCues(
                playShootSound:   !string.IsNullOrEmpty(_def.soundShoot),
                spawnShellCasing: false,
                spawnMuzzleFlash: false,
                makeNoise:        _def.noiseRadius > 0f,
                noiseRadius:      _def.noiseRadius / PpuScale,
                shineRadius:      0);

            Vector2 spawnPos = new Vector2(State.X, State.Y);

            if (_def.throwTip == 1)
            {
                // ── Mine placement ────────────────────────────────────────────
                // AS3: placed at (X,Y) — thrower's feet; reloadTime=75 arming delay.
                _plans.Add(new ShotPlan(
                    origin:        ShotOrigin.HoldPoint,
                    worldPosition: spawnPos,
                    angleRad:      0f,
                    kind:          ShotKind.Mine,
                    damage:        damCtx,
                    pelletIndex:   0,
                    totalPellets:  1,
                    speed:         0f,
                    gravity:       0f,
                    accel:         0f,
                    flame:         0,
                    navod:         0f,
                    springMode:    0,
                    bulletAnimated:false,
                    cues:          cues,
                    fuseFrames:    _def.fuseFrames > 0 ? _def.fuseFrames : 75,
                    isMine:        true
                ));
            }
            else
            {
                // ── Arc throw ─────────────────────────────────────────────────
                Vector2 throwVel = CalculateThrowVelocity(spawnPos, _lastAimTarget);

                _plans.Add(new ShotPlan(
                    origin:        ShotOrigin.ThrowPoint,
                    worldPosition: spawnPos,
                    angleRad:      Mathf.Atan2(throwVel.y, throwVel.x),
                    kind:          ShotKind.ThrownObject,
                    damage:        damCtx,
                    pelletIndex:   0,
                    totalPellets:  1,
                    speed:         throwVel.magnitude,
                    gravity:       1f,
                    accel:         0f,
                    flame:         0,
                    navod:         0f,
                    springMode:    0,
                    bulletAnimated:false,
                    cues:          cues,
                    throwVelocity: throwVel,
                    fuseFrames:    _def.fuseFrames > 0 ? _def.fuseFrames : 75,
                    isMine:        false
                ));
            }

            State.CurrentDurability = Mathf.Max(0, State.CurrentDurability - 1);
            State.TShoot  = 3;
            State.TAuto   = 3;
            State.IsShoot = true;
        }

        /// <summary>
        /// Emit a radio detonation signal.
        /// ProjectileSpawner detects FuseFrames==0 && IsMine==false and calls MineObject.DetonateAll.
        /// AS3: detonator() iterates loc.units, calls activate() on matching mines.
        /// </summary>
        private void EmitDetonation()
        {
            _plans.Add(new ShotPlan(
                origin:        ShotOrigin.HoldPoint,
                worldPosition: new Vector2(State.X, State.Y),
                angleRad:      0f,
                kind:          ShotKind.Mine,
                damage:        DamageContext.FromWeapon(_def, null),
                pelletIndex:   0,
                totalPellets:  1,
                speed:         0f,
                gravity:       0f,
                accel:         0f,
                flame:         0,
                navod:         0f,
                springMode:    0,
                bulletAnimated:false,
                cues:          ShotCues.None,
                fuseFrames:    0,      // 0 = detonation signal, not new placement
                isMine:        false
            ));
        }

        // ── Throw velocity calculation ─────────────────────────────────────────

        /// <summary>
        /// Mirrors WThrow.getVel() + getRot() + dx/dy setup.
        ///
        /// AS3 (Flash pixel space, 30fps):
        ///   vel = min(speed*skill, sqrt(dX²+dY²)/10*skill - dY/10*skill)
        ///   rotOffset = -dX / (vel + 0.0001) / 100
        ///   dx = cos(rot + rotOffset) * vel
        ///   dy = sin(rot + rotOffset) * vel
        ///
        /// Result converted to Unity units/sec.
        /// </summary>
        private Vector2 CalculateThrowVelocity(Vector2 spawnPos, Vector2 aimTarget)
        {
            float dX = aimTarget.x - spawnPos.x;
            float dY = aimTarget.y - spawnPos.y;

            // Convert to Flash pixel distances for the AS3 formula.
            float dXpx = dX * PpuScale;
            float dYpx = dY * PpuScale;

            float skill    = DefaultWeaponSkill;
            float speedPx  = _def.projectileSpeed;  // px/frame from definition

            // getVel: distance-based, gravity-adjusted, capped by base speed.
            float distBased = Mathf.Sqrt(dXpx * dXpx + dYpx * dYpx) / 10f * skill
                              - dYpx / 10f * skill;
            float velPx = Mathf.Max(1f, Mathf.Min(speedPx * skill, distBased));

            // getRot: gravity compensation angle offset.
            float rotOffset = -dXpx / (velPx + 0.0001f) / 100f;

            float aimAngle = State.Rot + rotOffset;

            // Convert px/frame → Unity units/sec.
            float velUnity = velPx / PpuScale * FlashFps;

            return new Vector2(Mathf.Cos(aimAngle), Mathf.Sin(aimAngle)) * velUnity;
        }

        // ── Ammo ──────────────────────────────────────────────────────────────

        private bool ConsumeAmmo()
        {
            if (_kolAmmo <= 0) return false;
            _kolAmmo--;
            return true;
        }
    }
}
