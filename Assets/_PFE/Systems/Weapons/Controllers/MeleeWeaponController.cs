using System.Collections.Generic;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.Entities.Weapons;

namespace PFE.Systems.Weapons.Controllers
{
    /// <summary>
    /// Full AS3-parity implementation of WClub.as for tip==1 melee weapons.
    ///
    /// Key behaviors from AS3:
    ///
    /// Position (spring physics):
    ///   del = (celTarget - weaponPos) clamped to meleeR reach
    ///   X += del.x;  Y += del.y   (weapon chases cursor-clamped target)
    ///   On first frame or krep>0: snap to holdPoint.
    ///
    /// mtip sub-behaviors:
    ///   0 = swing (club/sword) — rot sweeps arc driven by anim value
    ///   1 = thrust (spear)     — weapon lunges along aim axis
    ///   2 = slash (knife)      — hit fires once at TAttack==1
    ///
    /// Attack:
    ///   weaponAttack() → t_attack = rapid_act (= rapid, no multiplier here)
    ///   shoot() called immediately on weaponAttack for mtip==0/2
    ///   For mtip==1 shoot fires at t_attack==rapid/2 (thrust peak)
    ///
    /// Hit window (mtip==0):
    ///   active when t_attack is in range [rapid*1/6 .. rapid*5/6]
    ///   → emits MeleeSweep ShotPlan with MeleePrevTip / MeleeCurrTip each frame
    ///
    /// Combo system (meleeCombo=true):
    ///   combo counter increments each attack; at combo>=4: powerMult=2, reset
    ///   t_combo window = rapid + 20 frames between hits
    ///
    /// Power attack (meleePowerAttack=true):
    ///   pow accumulates while attack held; if 2 < pow < rapid*2.15: powerMult scales
    ///
    /// Animation:
    ///   anim drives rot via: rot = -PI/2 + (-PI/6 + anim*PI) * storona
    ///   anim is a 0→1 value that sweeps over the attack duration
    ///
    /// MeleeHitVolume:
    ///   Assigned by PlayerWeaponLoadout. Controller calls BindMove() each flash frame
    ///   during the active hit window. Outside the window, volume is disabled.
    /// </summary>
    public sealed class MeleeWeaponController : IWeaponController
    {
        // ── Constants ─────────────────────────────────────────────────────────

        private const float FlashFps = 30f;
        private const float PpuScale = 100f;

        // Default reach in Unity units when WeaponDefinition.meleeDlina not set.
        private const float DefaultReach = 1f;

        // ── State ─────────────────────────────────────────────────────────────

        public WeaponRuntimeState State { get; }
        private readonly WeaponDefinition _def;

        private float _frameAccum;
        private bool  _attackHeld;

        // Swing animation 0→1 value (anim in AS3).
        private float _anim;

        // Facing direction: +1 = right, -1 = left. Derived from aim target.
        private float _storona = 1f;

        // Combo state.
        private int _combo;
        private int _tCombo;

        // Power attack accumulator (mirrors pow in WeaponRuntimeState).
        // We track it locally since WeaponRuntimeState.Pow is already defined.

        // Power multiplier applied to this hit's damage.
        private float _powerMult = 1f;

        // Weapon tip positions for MeleeSweep (previous and current frame).
        private Vector2 _prevTip;
        private Vector2 _currTip;
        private bool    _inStrikeWindow;
        private bool    _firstFrame = true;

        // Reach in Unity units.
        private float _dlina;
        private float _minDlina;

        // External MeleeHitVolume — assigned by PlayerWeaponLoadout after equip.
        public MeleeHitVolume HitVolume { get; set; }

        private readonly List<ShotPlan> _plans = new();

        // ── Constructor ───────────────────────────────────────────────────────

        public MeleeWeaponController(WeaponRuntimeState state)
        {
            State    = state;
            _def     = state.Def;
            _dlina   = _def.meleeDlina   > 0f ? _def.meleeDlina   / PpuScale : DefaultReach;
            _minDlina = _def.meleeMinDlina > 0f ? _def.meleeMinDlina / PpuScale : _dlina;
        }

        // ── IWeaponController ─────────────────────────────────────────────────

        public void BeginAttack() => _attackHeld = true;
        public void EndAttack()   => _attackHeld = false;
        public void StartReload() { }

        public void Tick(float dt, Vector2 holdPoint, Vector2 hornPoint, Vector2 aimTarget)
        {
            _frameAccum += dt * FlashFps;
            int frames = Mathf.FloorToInt(_frameAccum);
            _frameAccum -= frames;

            for (int i = 0; i < frames; i++)
                TickOneFlashFrame(holdPoint, aimTarget);

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
            // ── Facing direction ──────────────────────────────────────────────
            _storona = aimTarget.x >= holdPoint.x ? 1f : -1f;

            // ── Spring position (AS3: del toward celTarget, clamped to meleeR) ──
            if (_firstFrame || State.TAttack <= 0)
            {
                State.X    = holdPoint.x;
                State.Y    = holdPoint.y;
                _firstFrame = false;
            }
            else
            {
                // Cursor target clamped to reach.
                Vector2 weaponPos = new Vector2(State.X, State.Y);
                Vector2 toTarget  = aimTarget - weaponPos;
                if (toTarget.magnitude > _dlina)
                    toTarget = toTarget.normalized * _dlina;

                Vector2 del = toTarget / 2f;   // AS3: (celX - X) / 2
                State.X += del.x;
                State.Y += del.y;
            }

            // ── Rotation (swing arc driven by anim) ───────────────────────────
            UpdateAnim();
            // AS3: rot = -PI/2 + (-PI/6 + anim*PI) * storona
            State.Rot   = -Mathf.PI / 2f + (-Mathf.PI / 6f + _anim * Mathf.PI) * _storona;
            State.Ready = true;

            // ── attack() ─────────────────────────────────────────────────────
            if (_attackHeld && State.TAttack <= 0)
                WeaponAttack(aimTarget, holdPoint);

            // ── actions() timers ──────────────────────────────────────────────
            RunActions(aimTarget);

            // ── Hit window & MeleeSweep emission ─────────────────────────────
            UpdateHitWindow(holdPoint);

            State.IsShoot   = false;
            State.WasAttack = State.IsAttack;
            State.IsAttack  = false;
        }

        // ── Attack initiation ─────────────────────────────────────────────────

        /// <summary>
        /// Mirrors WClub.weaponAttack().
        /// Sets t_attack, applies combo/power multipliers, calls Shoot() for swing weapons.
        /// </summary>
        private void WeaponAttack(Vector2 aimTarget, Vector2 holdPoint)
        {
            if (State.IsBroken) return;

            _powerMult = 1f;

            // ── Combo (combinat) ──────────────────────────────────────────────
            if (_def.meleeCombo)
            {
                _tCombo = Mathf.RoundToInt(_def.rapid) + 20;
                _combo++;
                if (_combo >= 4)
                {
                    _powerMult = 2f;
                    _combo     = 0;
                }
            }

            // ── Power attack (powerfull) ─────────────────────────────────────
            if (_def.meleePowerAttack && State.Pow > 2 && State.Pow < _def.rapid * 2.15f)
            {
                _powerMult = 1f + State.Pow / (_def.rapid * 2.15f);
            }

            State.TAttack  = Mathf.RoundToInt(_def.rapid);
            State.IsAttack = true;
            _anim          = 0f;

            // Horizontal (mtip==0) and Overhead/slash (mtip==2) emit hit on attack start.
            if (_def.meleeType == MeleeType.Horizontal || _def.meleeType == MeleeType.Overhead)
                Shoot(aimTarget, holdPoint, isInstant: _def.meleeType == MeleeType.Overhead);

            State.CurrentDurability = Mathf.Max(0, State.CurrentDurability - 1);
            State.KolShoot++;
            State.TShoot = 3;
            State.IsShoot = true;
        }

        // ── Shoot (hit detection) ─────────────────────────────────────────────

        /// <summary>
        /// Emit a MeleeSweep ShotPlan for MeleeHitVolume to process.
        /// isInstant: slash fires once at attack start (TAttack==1 in AS3).
        /// </summary>
        private void Shoot(Vector2 aimTarget, Vector2 holdPoint, bool isInstant = false)
        {
            Vector2 tipPos = CalculateTipPosition(holdPoint, aimTarget);

            DamageContext baseDmg  = DamageContext.FromWeapon(_def, null);
            // Apply power / combo multiplier to base damage.
            DamageContext finalDmg = _powerMult != 1f
                ? new DamageContext(
                    baseDmg.Owner, baseDmg.Weapon,
                    baseDmg.BaseDamage * _powerMult,
                    baseDmg.ExplosionDamage,
                    baseDmg.ArmorMultiplier,
                    baseDmg.Piercing,
                    baseDmg.Knockback * _powerMult,
                    baseDmg.KnockbackDir,
                    baseDmg.CritChance,
                    baseDmg.CritMultiplier,
                    baseDmg.DamageType,
                    baseDmg.DestroyTiles,
                    baseDmg.PenetrationChance,
                    baseDmg.DopEffect,
                    baseDmg.DopDamage,
                    baseDmg.DopChance)
                : baseDmg;

            ShotCues cues = new ShotCues(
                playShootSound:   !string.IsNullOrEmpty(_def.soundShoot),
                spawnShellCasing: false,
                spawnMuzzleFlash: false,
                makeNoise:        _def.noiseRadius > 0f,
                noiseRadius:      _def.noiseRadius / PpuScale,
                shineRadius:      0);

            _plans.Add(new ShotPlan(
                origin:        ShotOrigin.HoldPoint,
                worldPosition: new Vector2(State.X, State.Y),
                angleRad:      State.Rot,
                kind:          ShotKind.MeleeSweep,
                damage:        finalDmg,
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
                meleePrevTip:  _prevTip,
                meleeCurrTip:  tipPos
            ));

            _prevTip = tipPos;
        }

        // ── Actions (timers) ──────────────────────────────────────────────────

        private void RunActions(Vector2 aimTarget)
        {
            // Thrust (mtip==1): fire at midpoint of attack.
            if (_def.meleeType == MeleeType.Thrust &&
                State.TAttack == Mathf.RoundToInt(_def.rapid / 2f))
                Shoot(aimTarget, new Vector2(State.X, State.Y));

            if (State.TAttack > 0) State.TAttack--;
            if (State.TRet    > 0) State.TRet--;
            if (State.TShoot  > 0) State.TShoot--;

            // Combo window countdown.
            if (_tCombo > 0)
            {
                _tCombo--;
                if (_tCombo <= 0)
                    _combo = 0;
            }

            // Pow accumulator: increments while attack held but TAttack cooling down.
            if (_attackHeld && State.TAttack == 0)
                State.Pow++;
            else if (State.TAttack == 0)
                State.Pow = 0;

            State.RotUp = 0f;  // Melee weapons don't use recoil lift.
        }

        // ── Hit window ────────────────────────────────────────────────────────

        /// <summary>
        /// During the strike window (middle third of attack duration) emit MeleeSweep
        /// each frame so MeleeHitVolume sweeps between previous and current tip.
        /// AS3: active between rapid*1/6 < t_attack < rapid*5/6.
        /// </summary>
        private void UpdateHitWindow(Vector2 holdPoint)
        {
            if (State.TAttack <= 0 || _def.meleeType != MeleeType.Horizontal)
            {
                if (_inStrikeWindow)
                {
                    HitVolume?.SetActive(false);
                    _inStrikeWindow = false;
                }
                return;
            }

            float rapid = _def.rapid;
            bool inWindow = State.TAttack < rapid * 5f / 6f &&
                            State.TAttack > rapid * 1f / 6f;

            if (inWindow)
            {
                Vector2 tip = CalculateTipPosition(holdPoint, new Vector2(
                    State.X + Mathf.Cos(State.Rot) * _dlina,
                    State.Y + Mathf.Sin(State.Rot) * _dlina));

                if (!_inStrikeWindow)
                {
                    _prevTip        = tip;
                    _inStrikeWindow = true;
                    HitVolume?.SetActive(true);
                }

                HitVolume?.BindMove(_prevTip, tip);
                _prevTip = tip;
            }
            else if (_inStrikeWindow)
            {
                HitVolume?.SetActive(false);
                _inStrikeWindow = false;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Animate the swing arc. anim goes 0→1 over attack duration.
        /// AS3: three-phase formula driven by t_attack vs rapid_act.
        /// </summary>
        private void UpdateAnim()
        {
            if (State.TAttack <= 0)
            {
                _anim = 0f;
                return;
            }

            float t    = State.TAttack;
            float rap  = Mathf.Max(1f, _def.rapid);
            float norm = t / rap;   // 1→0 as attack progresses

            // AS3 three-phase:
            //   late  (norm >= 5/6): anim = -norm * 1.5
            //   mid   (norm >= 1/2): anim = -0.25 + (1-norm-1/6) * 3.75
            //   early (norm <  1/2): anim = (1 - norm*2)
            if (norm >= 5f / 6f)
                _anim = -(1f - norm) * 1.5f;
            else if (norm >= 0.5f)
                _anim = -0.25f + ((1f - norm) - 1f / 6f) * 3.75f;
            else
                _anim = 1f - norm * 2f;
        }

        /// <summary>
        /// World position of weapon tip at current rotation + dlina.
        /// </summary>
        private Vector2 CalculateTipPosition(Vector2 origin, Vector2 aimTarget)
        {
            float angle = Mathf.Atan2(aimTarget.y - origin.y, aimTarget.x - origin.x);
            return origin + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * _dlina;
        }
    }
}
