using System.Collections.Generic;
using UnityEngine;
using PFE.Data.Definitions;

namespace PFE.Systems.Weapons.Controllers
{
    /// <summary>
    /// Full AS3-parity implementation of WPunch.as for unarmed/punch weapons (tip==Internal).
    ///
    /// Key behaviors from AS3:
    ///
    /// No held sprite:
    ///   animate() is empty in WPunch.as — WeaponPresenter disables the renderer.
    ///
    /// attack() (lines 67-74 WPunch.as):
    ///   Sets t_attack = rapid unconditionally. No ammo, no prep, no magazine.
    ///
    /// shoot() timing (lines 28-64 WPunch.as):
    ///   Fires at t_attack == rapid - 5 (5 frames before attack end).
    ///   Calculates aim angle from owner toward celX/celY, clamps vertical to
    ///   horizontal magnitude so you can't punch straight up/down.
    ///   Emits ShotPlan { Kind = MeleeSweep } with a 5-frame lifespan.
    ///
    /// zadok back-kick (line 47):
    ///   If aim direction is opposite to owner's facing (storona), apply:
    ///   damage ×2, knockback ×1.5.
    ///   Detection: angle vs facing: punch left while facing right (or vice versa).
    ///
    /// WKick variant:
    ///   Same controller, fires at t_attack == rapid - 8 instead of rapid - 5.
    ///   isKick flag set from WeaponDefinition (no separate class needed).
    /// </summary>
    public sealed class UnarmedWeaponController : IWeaponController
    {
        // ── Constants ─────────────────────────────────────────────────────────

        private const float FlashFps = 30f;
        private const float PpuScale = 100f;

        // Punch hit range in Unity units (short range — fist reach).
        private const float PunchRange   = 0.6f;
        // Kick fires 8 frames before end; punch fires 5.
        private const int   PunchFireOffset = 5;
        private const int   KickFireOffset  = 8;

        // ── State ─────────────────────────────────────────────────────────────

        public WeaponRuntimeState State { get; }
        private readonly WeaponDefinition _def;

        private float _frameAccum;
        private bool  _attackHeld;
        private bool  _attackJustPressed;
        private Vector2 _lastAimTarget;
        private Vector2 _lastHoldPoint;

        // Facing: +1 right, -1 left — derived from aim vs hold point.
        private float _storona = 1f;

        private readonly List<ShotPlan> _plans = new();

        // ── Constructor ───────────────────────────────────────────────────────

        public UnarmedWeaponController(WeaponRuntimeState state)
        {
            State = state;
            _def  = state.Def;
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
            _lastHoldPoint = holdPoint;
            _storona       = aimTarget.x >= holdPoint.x ? 1f : -1f;

            // Unarmed: no world position tracking — position follows owner.
            State.X = holdPoint.x;
            State.Y = holdPoint.y;
            State.Rot   = Mathf.Atan2(aimTarget.y - holdPoint.y, aimTarget.x - holdPoint.x);
            State.Ready = true;

            // ── attack() ─────────────────────────────────────────────────────
            if (_attackHeld && State.TAttack <= 0)
                RunAttack();

            // ── actions() ────────────────────────────────────────────────────
            RunActions();

            State.IsShoot   = false;
            State.WasAttack = State.IsAttack;
            State.IsAttack  = false;
        }

        // ── attack() ─────────────────────────────────────────────────────────

        /// <summary>
        /// Mirrors WPunch.attack(): sets t_attack = rapid unconditionally.
        /// </summary>
        private void RunAttack()
        {
            // Single-shot debounce (non-auto fists — tap timing).
            bool isAuto = _def.rapid <= 6;
            if (!isAuto && State.TAuto > 0)
            {
                State.TAuto = 3;
                return;
            }

            State.TAttack  = Mathf.RoundToInt(_def.rapid);
            State.IsAttack = true;
            State.KolShoot++;
            State.TShoot = 3;
        }

        // ── actions() ────────────────────────────────────────────────────────

        private void RunActions()
        {
            // ── Shoot trigger ─────────────────────────────────────────────────
            // AS3: fires at t_attack == rapid - 5 (or rapid - 8 for kick).
            int fireOffset = PunchFireOffset;  // WKick would use KickFireOffset
            if (State.TAttack == Mathf.RoundToInt(_def.rapid) - fireOffset)
                Shoot();

            if (State.TAttack > 0) State.TAttack--;
            if (State.TRet    > 0) State.TRet--;
            if (State.TShoot  > 0) State.TShoot--;
            if (State.TAuto   > 0) State.TAuto--;
            else                    State.Pow = 0;

            State.RotUp = 0f;  // No recoil lift on unarmed.
        }

        // ── shoot() ──────────────────────────────────────────────────────────

        /// <summary>
        /// Mirrors WPunch.shoot().
        /// Calculates punch angle (vertical clamped to horizontal magnitude),
        /// checks zadok back-kick, emits MeleeSweep ShotPlan.
        /// </summary>
        private void Shoot()
        {
            Vector2 holdPos = _lastHoldPoint;
            Vector2 aim     = _lastAimTarget;

            // ── Aim angle calculation (AS3 lines 37-44) ───────────────────────
            float dX = aim.x - holdPos.x;
            float dY = aim.y - holdPos.y;

            // Clamp vertical to horizontal magnitude (can't punch straight up/down).
            float dYclamped = Mathf.Abs(dY) > Mathf.Abs(dX)
                ? Mathf.Abs(dX) * Mathf.Sign(dY)
                : dY;

            float punchAngle = Mathf.Atan2(dYclamped, dX);

            // ── Zadok back-kick detection (AS3 lines 47-55) ───────────────────
            // Back-kick: punch direction is opposite to facing direction.
            // Facing right (_storona > 0): punch angle > PI/2 or < -PI/2 → back kick.
            // Facing left  (_storona < 0): punch angle in (-PI/2, PI/2)   → back kick.
            bool zadok = false;
            if (_storona > 0 && (punchAngle > Mathf.PI / 2f || punchAngle < -Mathf.PI / 2f))
                zadok = true;
            else if (_storona < 0 && punchAngle > -Mathf.PI / 2f && punchAngle < Mathf.PI / 2f)
                zadok = true;

            // ── Build damage context ──────────────────────────────────────────
            DamageContext baseDmg = DamageContext.FromWeapon(_def, null);

            float finalDamage   = baseDmg.BaseDamage;
            float finalKnockback = baseDmg.Knockback;

            if (zadok)
            {
                // AS3: b.damage = damage*2; b.otbros *= 1.5;
                finalDamage   *= 2f;
                finalKnockback *= 1.5f;
            }

            DamageContext dmgCtx = new DamageContext(
                baseDmg.Owner,
                baseDmg.Weapon,
                finalDamage,
                baseDmg.ExplosionDamage,
                baseDmg.ArmorMultiplier,
                baseDmg.Piercing,
                finalKnockback,
                new Vector2(Mathf.Cos(punchAngle), Mathf.Sin(punchAngle)),
                baseDmg.CritChance,
                baseDmg.CritMultiplier,
                baseDmg.DamageType,
                baseDmg.DestroyTiles,
                baseDmg.PenetrationChance,
                baseDmg.DopEffect,
                baseDmg.DopDamage,
                baseDmg.DopChance);

            // ── Emit ShotPlan ─────────────────────────────────────────────────
            // MeleeSweep — MeleeHitVolume handles the actual hit detection.
            // WorldPosition is the punch impact point (arm reach from body).
            Vector2 impactPos = holdPos + new Vector2(
                Mathf.Cos(punchAngle), Mathf.Sin(punchAngle)) * PunchRange;

            ShotCues cues = new ShotCues(
                playShootSound:   !string.IsNullOrEmpty(_def.soundShoot),
                spawnShellCasing: false,
                spawnMuzzleFlash: false,
                makeNoise:        _def.noiseRadius > 0f,
                noiseRadius:      _def.noiseRadius / PpuScale,
                shineRadius:      0);

            _plans.Add(new ShotPlan(
                origin:        ShotOrigin.HoldPoint,
                worldPosition: impactPos,
                angleRad:      punchAngle,
                kind:          ShotKind.MeleeSweep,
                damage:        dmgCtx,
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
                meleePrevTip:  holdPos,
                meleeCurrTip:  impactPos
            ));

            State.IsShoot = true;
        }
    }
}
