using System.Collections.Generic;
using UnityEngine;
using PFE.Core;
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
    /// mtip sub-behaviors — all three drive the hit volume (see UpdateHitWindow):
    ///   0 = swing (club/sword) — rot sweeps an arc driven by anim
    ///   1 = thrust (spear)     — rot points at the cursor, tip lunges by anim * atDlina
    ///   2 = overhead (axe/saw) — ONE bindMove from mindlina to dlina at TAttack == 1
    ///
    /// Attack:
    ///   weaponAttack() → t_attack = rapid_act (= rapid, no multiplier here)
    ///   mtip==0 emits its hit plan on weaponAttack; mtip==1 at t_attack == rapid/2 (thrust peak);
    ///   mtip==2 at t_attack == 1. AS3 drives all three from t_attack, not from attack start.
    ///
    /// Hit window:
    ///   mtip 0: rapid*1/6 &lt; t_attack &lt; rapid*5/6  (AS3 uses rapid_act/2 .. 5/6 — audit §4)
    ///   mtip 1: rapid/2   &lt;= t_attack &lt; rapid*5/6
    ///   mtip 2: t_attack == 1, once
    ///   Each active frame emits a MeleeSweep ShotPlan and calls MeleeHitVolume.BindMove().
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

        private const float PpuScale = 100f;

        // Default reach in Unity units when WeaponDefinition.meleeDlina not set.
        private const float DefaultReach = 1f;

        // AS3 WClub.atDlina (WClub.as:59) — the lunge distance the thrust tip travels along the
        // aim axis, in AS3 pixels. Only mtip==1 uses it (WClub.as:433-434).
        private const float AtDlinaPx = 100f;

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

        /// <summary>WClub.atDlina in Unity units — the mtip==1 thrust lunge distance.</summary>
        private float AtDlina => AtDlinaPx / PpuScale;

        // External hit volume — assigned by PlayerWeaponLoadout after equip.
        // Typed as the interface so EditMode tests can substitute a recording stub; the real
        // MeleeHitVolume caches its collider in Awake, which never runs for AddComponent in EditMode.
        public IMeleeHitVolume HitVolume { get; set; }

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
            _frameAccum += dt * SimClock.FramesPerSecond;
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

            // ── Rotation ──────────────────────────────────────────────────────
            // AS3 sets `rot` inside each mtip branch (WClub.as:377 / :430 / :466):
            //   mtip 0 → the anim-driven swing arc
            //   mtip 1 / 2 → straight at the cursor
            // AS3 aims the latter from `owner.Y - owner.scY/2`; this controller has no owner
            // offset, so it aims from the weapon's own position like the rest of the class.
            UpdateAnim();
            State.Rot = _def.meleeType == MeleeType.Horizontal
                ? -Mathf.PI / 2f + (-Mathf.PI / 6f + _anim * Mathf.PI) * _storona
                : Mathf.Atan2(aimTarget.y - State.Y, aimTarget.x - State.X);
            State.Ready = true;

            // ── attack() ─────────────────────────────────────────────────────
            if (_attackHeld && State.TAttack <= 0)
                WeaponAttack(aimTarget, holdPoint);

            // ── actions() timers ──────────────────────────────────────────────
            RunActions(aimTarget);

            // ── Hit window & MeleeSweep emission ─────────────────────────────
            UpdateHitWindow(holdPoint, aimTarget);

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

            // Only mtip==0 emits its hit plan at attack start. AS3 drives all three sub-types from
            // t_attack rather than from attack start: mtip==1 fires at the thrust peak (RunActions)
            // and mtip==2 fires once at t_attack==1 (UpdateHitWindow). WClub.as:435-483.
            if (_def.meleeType == MeleeType.Horizontal)
                Shoot(aimTarget, holdPoint);

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

            DamageContext baseDmg  = DamageContext.FromWeapon(_def, null, State.OwnerFaction);
            // Apply power / combo multiplier to base damage. Scaled through one method rather than a
            // positional re-construction: the 16-argument copy this replaced silently dropped
            // ownerFaction, and would drop every field added to DamageContext afterwards.
            DamageContext finalDmg = baseDmg.WithScaledDamage(_powerMult, _powerMult);

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
        /// Per-subtype strike window and tip sweep, mirroring the three branches of
        /// WClub.actions(). Each branch drives MeleeHitVolume through BindMove(prev, curr).
        ///
        ///   mtip 0 Horizontal — WClub.as:378-408: a fan of kolvzz+1 lines along the swing arc,
        ///                       active while rapid_act/2 &lt;= t_attack &lt; rapid_act*5/6.
        ///   mtip 1 Thrust     — WClub.as:435-454: ONE line at X + cos2*dlina + plX, where
        ///                       plX/plY = cos2/sin2 * anim * atDlina (the lunge). Same window.
        ///   mtip 2 Overhead   — WClub.as:469-483: ONE bindMove from mindlina to dlina, taken when
        ///                       t_attack == 1 — i.e. once, at the END of the attack.
        ///
        /// This method used to early-return for every meleeType except Horizontal, and
        /// <c>HitVolume.SetActive(true)</c> exists only past that guard — so Thrust and Overhead
        /// had a permanently disabled hit volume and could never hit anything (spear / mspear /
        /// tlance, autoaxe / bsaw / ripper). Audit §3.
        ///
        /// NOTE: AS3's thresholds use <c>rapid_act</c> (resultRapid() plus the water doubling), and
        /// this controller has no rapid_act, so all three branches use <c>_def.rapid</c>. That
        /// divergence is audit §4 and must be converted for all branches together, not one at a time.
        /// </summary>
        private void UpdateHitWindow(Vector2 holdPoint, Vector2 aimTarget)
        {
            if (State.TAttack <= 0)
            {
                EndStrikeWindow();
                return;
            }

            switch (_def.meleeType)
            {
                case MeleeType.Thrust:
                    UpdateThrustWindow(aimTarget);
                    break;

                case MeleeType.Overhead:
                    UpdateOverheadWindow(holdPoint, aimTarget);
                    break;

                default:   // MeleeType.Horizontal
                    UpdateHorizontalWindow(holdPoint);
                    break;
            }
        }

        /// <summary>mtip 0 — the swing-arc sweep (WClub.as:378-408).</summary>
        private void UpdateHorizontalWindow(Vector2 holdPoint)
        {
            float rapid = _def.rapid;
            bool inWindow = State.TAttack < rapid * 5f / 6f &&
                            State.TAttack > rapid * 1f / 6f;

            if (!inWindow)
            {
                EndStrikeWindow();
                return;
            }

            Vector2 tip = CalculateTipPosition(holdPoint, new Vector2(
                State.X + Mathf.Cos(State.Rot) * _dlina,
                State.Y + Mathf.Sin(State.Rot) * _dlina));

            BeginOrExtendStrike(tip);
        }

        /// <summary>mtip 1 — the lunge: one line pushed out by anim * atDlina (WClub.as:435-454).</summary>
        private void UpdateThrustWindow(Vector2 aimTarget)
        {
            float rapid = _def.rapid;
            if (State.TAttack < rapid / 2f || State.TAttack >= rapid * 5f / 6f)
            {
                EndStrikeWindow();
                return;
            }

            // AS3: _loc6_ = X + cos2 * dlina + plX, with plX = cos2 * anim * atDlina.
            // Factoring cos2 out gives dir * (dlina + anim * atDlina) along the aim axis.
            Vector2 weaponPos = new Vector2(State.X, State.Y);
            Vector2 tip = weaponPos + DirectionTo(aimTarget, weaponPos) * (_dlina + _anim * AtDlina);

            BeginOrExtendStrike(tip);
        }

        /// <summary>
        /// mtip 2 — one bindMove from mindlina to dlina, taken on the frame t_attack == 1
        /// (WClub.as:469-483). This is also where the hit plan is emitted, because AS3 fires at the
        /// END of the overhead swing, not at attack start.
        /// </summary>
        private void UpdateOverheadWindow(Vector2 holdPoint, Vector2 aimTarget)
        {
            if (State.TAttack != 1)
            {
                EndStrikeWindow();
                return;
            }

            Shoot(aimTarget, holdPoint, isInstant: true);

            Vector2 weaponPos = new Vector2(State.X, State.Y);
            Vector2 dir       = DirectionTo(aimTarget, weaponPos);

            HitVolume?.SetActive(true);
            HitVolume?.BindMove(weaponPos + dir * _minDlina, weaponPos + dir * _dlina);

            _prevTip        = weaponPos + dir * _dlina;
            _inStrikeWindow = true;
        }

        /// <summary>Open the strike window on the first active frame, then sweep prevTip → tip.</summary>
        private void BeginOrExtendStrike(Vector2 tip)
        {
            if (!_inStrikeWindow)
            {
                _prevTip        = tip;
                _inStrikeWindow = true;
                HitVolume?.SetActive(true);
            }

            HitVolume?.BindMove(_prevTip, tip);
            _prevTip = tip;
        }

        private void EndStrikeWindow()
        {
            if (!_inStrikeWindow) return;
            HitVolume?.SetActive(false);
            _inStrikeWindow = false;
        }

        /// <summary>Unit vector from origin toward target, falling back to +X when they coincide.</summary>
        private static Vector2 DirectionTo(Vector2 target, Vector2 origin)
        {
            Vector2 d = target - origin;
            return d.sqrMagnitude > 1e-8f ? d.normalized : Vector2.right;
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
