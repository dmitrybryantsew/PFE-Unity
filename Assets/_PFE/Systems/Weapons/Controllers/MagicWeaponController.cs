using System.Collections.Generic;
using UnityEngine;
using PFE.Data.Definitions;

namespace PFE.Systems.Weapons.Controllers
{
    /// <summary>
    /// Full AS3-parity implementation of WMagic.as for tip==5 magic weapons.
    ///
    /// Key differences from RangedWeaponController:
    ///   - Position SNAPS to hornPoint every tick (no lerp) — AS3: X = owner.magicX; Y = owner.magicY
    ///   - Rotation still calculated (atan2 to aim target) but WeaponPresenter applies it normally
    ///   - Dual resource cost: magicPoolCost (owner.mana) + manaHealthCost (player health-mana pool)
    ///   - Resource failure → t_rel = t_prep * 3 (lockout timer)
    ///   - Mana is consumed AFTER the shot is emitted (mirrors WMagic.shoot() calling super first)
    ///   - No magazine/reload — magic weapons are resource-gated, not ammo-gated
    ///
    /// AS3 source: WMagic.as + Weapon.as (tip==5 branches)
    /// </summary>
    public sealed class MagicWeaponController : IWeaponController
    {
        // ── Constants ─────────────────────────────────────────────────────────

        private const float FlashFps  = 30f;
        private const float PpuScale  = 100f;

        // ── State ─────────────────────────────────────────────────────────────

        public WeaponRuntimeState State { get; }
        private readonly WeaponDefinition _def;

        private float _frameAccum;
        private bool  _attackHeld;

        // Mana pools — simplified: no external ICharacterStats yet.
        // These will be driven by CharacterStats in a future stage.
        // For now, a local pool so magic weapons work without the full stats system.
        private float _manaPool;
        private float _manaHP;        // health-mana pool (pers.manaHP)

        private const float DefaultManaPool = 100f;
        private const float DefaultManaHP   = 100f;

        private readonly List<ShotPlan> _plans = new();

        // ── Constructor ───────────────────────────────────────────────────────

        public MagicWeaponController(WeaponRuntimeState state)
        {
            State    = state;
            _def     = state.Def;
            _manaPool = DefaultManaPool;
            _manaHP   = DefaultManaHP;
        }

        // ── IWeaponController ─────────────────────────────────────────────────

        public void BeginAttack() => _attackHeld = true;
        public void EndAttack()   => _attackHeld = false;
        public void StartReload() { }  // Magic weapons don't reload

        public void Tick(float dt, Vector2 holdPoint, Vector2 hornPoint, Vector2 aimTarget)
        {
            _frameAccum += dt * FlashFps;
            int frames = Mathf.FloorToInt(_frameAccum);
            _frameAccum -= frames;

            for (int i = 0; i < frames; i++)
                TickOneFlashFrame(hornPoint, aimTarget);

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

        // ── Core per-flash-frame logic ─────────────────────────────────────────

        private void TickOneFlashFrame(Vector2 hornPoint, Vector2 aimTarget)
        {
            // ── Position: SNAP to horn point every frame (no lerp) ────────────
            // AS3: X = owner.magicX; Y = owner.magicY  (inside tip==5 branch)
            State.X = hornPoint.x;
            State.Y = hornPoint.y;

            // ── Rotation toward aim (same atan2 as Weapon.as line 1074) ───────
            State.Rot   = Mathf.Atan2(aimTarget.y - State.Y, aimTarget.x - State.X);
            State.Ready = true;  // Magic weapons don't have drot — always ready

            // ── attack() ─────────────────────────────────────────────────────
            if (_attackHeld && State.TRel <= 0 && State.IsBroken == false)
                RunAttack();

            // ── actions() ────────────────────────────────────────────────────
            RunActions();

            State.IsShoot  = false;
            State.WasAttack = State.IsAttack;
            State.IsAttack  = false;
        }

        /// <summary>
        /// Mirrors WMagic.attack().
        /// Checks spell availability and mana, sets TAttack if OK, TRel if not.
        /// </summary>
        private void RunAttack()
        {
            // spellsPoss check: skipped — no spell unlock system yet.
            // In AS3: if (World.w.pers.spellsPoss == 0) return;

            State.IsAttack = true;

            // Prep charge (same as ranged).
            if (State.TPrep < _def.prepFrames + 10)
                State.TPrep += 2;

            // Only arm t_attack once prep is satisfied and no attack pending.
            if (State.TPrep < _def.prepFrames) return;
            if (State.TAttack > 0)             return;

            // ── Mana HP check (dmana > pers.manaHP) ─────────────────────────
            // AS3: if (owner.player && dmana > World.w.pers.manaHP) { t_rel = t_prep*3; }
            if (_def.manaHealthCost > 0f && _def.manaHealthCost > _manaHP)
            {
                State.TRel = State.TPrep * 3;
                Debug.Log("[MagicWeaponController] No mana HP — locked out.");
                return;
            }

            // ── Mana pool check (dmagic <= owner.mana) ───────────────────────
            // AS3: else if (dmagic <= owner.mana || owner.mana >= owner.maxmana * 0.99)
            bool hasEnoughMana = _def.magicPoolCost <= _manaPool ||
                                 _manaPool >= DefaultManaPool * 0.99f;
            if (!hasEnoughMana)
            {
                State.TRel = State.TPrep * 3;
                Debug.Log("[MagicWeaponController] Not enough mana — locked out.");
                return;
            }

            // ── Arm t_attack ──────────────────────────────────────────────────
            int burstFrames = _def.burstCount <= 0
                ? Mathf.RoundToInt(_def.rapid)
                : Mathf.RoundToInt(_def.rapid) * (_def.burstCount + 1);

            State.TAttack = burstFrames;
        }

        /// <summary>
        /// Mirrors the timer-management + shoot-trigger block of Weapon.actions() for tip==5.
        /// </summary>
        private void RunActions()
        {
            // ── Shoot trigger (same logic as RangedWeaponController) ──────────
            if (State.TAttack > 0)
            {
                bool isBurst = _def.burstCount > 0;
                if (!isBurst && State.TAttack == Mathf.RoundToInt(_def.rapid))
                    Shoot();
                else if (isBurst && State.TAttack > Mathf.RoundToInt(_def.rapid)
                         && State.TAttack % Mathf.RoundToInt(_def.rapid) == 0)
                    Shoot();
            }

            // ── Countdown timers ──────────────────────────────────────────────
            if (State.TAttack > 0) State.TAttack--;
            if (State.TRel    > 0) State.TRel--;
            if (State.TRet    > 0) State.TRet--;
            if (State.TShoot  > 0) State.TShoot--;

            // ── RotUp decay ───────────────────────────────────────────────────
            if      (State.RotUp > 5f)   State.RotUp *= 0.9f;
            else if (State.RotUp > 0.5f) State.RotUp -= 0.5f;
            else                          State.RotUp  = 0f;

            // ── Prep decay ────────────────────────────────────────────────────
            if (State.TPrep > 0) State.TPrep--;
            else                 State.KolShoot = 0;

            // ── TAuto ─────────────────────────────────────────────────────────
            if (State.TAuto > 0) State.TAuto--;
            else                 State.Pow = 0;
        }

        /// <summary>
        /// Mirrors WMagic.shoot(): calls base shoot logic, then consumes mana on success.
        /// </summary>
        private void Shoot()
        {
            // Durability check.
            if (State.IsBroken) return;

            // Build damage context and cues.
            DamageContext damCtx = DamageContext.FromWeapon(_def, null);

            ShotCues cues = new ShotCues(
                playShootSound:   !string.IsNullOrEmpty(_def.soundShoot),
                spawnShellCasing: false,
                spawnMuzzleFlash: !string.IsNullOrEmpty(_def.muzzleFlareId),
                makeNoise:        _def.noiseRadius > 0f,
                noiseRadius:      _def.noiseRadius / PpuScale,
                shineRadius:      _def.shineRadius);

            // Magic weapons fire from the horn point (HornPoint origin).
            int pellets = Mathf.Max(1, _def.projectilesPerShot);
            for (int i = 0; i < pellets; i++)
            {
                float spreadOffset = pellets > 1
                    ? (i - (pellets - 1) / 2f) * _def.deviation * Mathf.PI / 360f
                    : 0f;
                float pelletAngle = State.Rot + spreadOffset;

                _plans.Add(new ShotPlan(
                    origin:        ShotOrigin.HornPoint,
                    worldPosition: new Vector2(State.X, State.Y),
                    angleRad:      pelletAngle,
                    kind:          ShotKind.Projectile,
                    damage:        damCtx,
                    pelletIndex:   i,
                    totalPellets:  pellets,
                    speed:         _def.projectileSpeed / PpuScale,
                    gravity:       _def.bulletGravity,
                    accel:         _def.bulletAccel / PpuScale,
                    flame:         _def.bulletFlame,
                    navod:         _def.bulletNavod,
                    springMode:    _def.springMode,
                    bulletAnimated:_def.bulletAnimated,
                    cues:          i == 0 ? cues : ShotCues.None
                ));
            }

            // ── Consume mana AFTER emitting (mirrors WMagic.shoot() calling super first) ──
            // AS3: owner.mana -= dmagic; World.w.pers.manaDamage(dmana);
            _manaPool = Mathf.Max(0f, _manaPool - _def.magicPoolCost);
            _manaHP   = Mathf.Max(0f, _manaHP   - _def.manaHealthCost);

            // ── Post-shot state ───────────────────────────────────────────────
            State.CurrentDurability = Mathf.Max(0, State.CurrentDurability - 1);
            State.TRet   = _def.recoilFrames;
            State.RotUp += _def.recoilLift;
            if (_def.weaponVisual != null && _def.weaponVisual.shootFrameStart >= 0 && State.TShoot <= 1)
                State.TShoot = 3;
            State.KolShoot++;
            State.TAuto   = 3;
            State.IsShoot = true;
        }
    }
}
