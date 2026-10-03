using System.Collections.Generic;
using UnityEngine;
using PFE.Core;
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
    ///
    /// Owner multipliers (WKick.as:47-69):
    ///   WIRED     punchDamMult → b.damage and b.otbros, both scaled by PunchDamMult.
    ///   BLOCKED   punchDamMult → the stun proc (dopCh = punchDamMult - 1, dopDamage = 30/60).
    ///             DamageContext.DopChance / DopDamage have no reader anywhere in the port, so
    ///             writing them here would change nothing observable.
    ///   BLOCKED   kickDestroy → b.destroy. DamageContext.DestroyTiles is read only by the
    ///             projectile path (ProjectileFactory.cs:81); a MeleeSweep plan never becomes a
    ///             projectile, so no melee or unarmed hit destroys tiles today.
    ///   Those two are exposed on IWeaponStatSource so the fix is a one-liner once their consumers
    ///   land, but they must not be counted as closed divergences. See TOPIC_weapons_dispatch.
    /// </summary>
    public sealed class UnarmedWeaponController : IWeaponController
    {
        // ── Constants ─────────────────────────────────────────────────────────

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

        // ── Owner stats ───────────────────────────────────────────────────────

        /// <summary>
        /// The wielder's RPG multipliers, or null for a weapon with no owner (an enemy, or a test).
        ///
        /// <para>AS3 reads these straight off <c>World.w.pers</c> inside <c>WKick.actions()</c>
        /// (<c>WKick.as:47-54,69</c>) rather than copying them in <c>setPers</c> — the punch/kick
        /// family is the one weapon type that reaches into <c>Pers</c> at fire time. The port holds a
        /// live reference for the same reason the ranged controller does.</para>
        /// </summary>
        private readonly IWeaponStatSource _statSource;

        /// <summary><c>Pers.punchDamMult</c> (<c>WKick.as:47-48</c>), 1 when there is no owner.</summary>
        private float PunchDamMult => _statSource != null ? _statSource.PunchDamMult : 1f;

        // NOTE: `Pers.kickDestroy` (WKick.as:69, b.destroy) is deliberately NOT read here even
        // though IWeaponStatSource exposes it. Its consumer is DamageContext.DestroyTiles, which only
        // the projectile path reads (ProjectileFactory.cs:81) — a MeleeSweep plan never becomes a
        // projectile, so any value written here would be inert. The seam carries it so the fix is a
        // one-liner once melee tile destruction lands; wire it then, not now.

        // ── Constructor ───────────────────────────────────────────────────────

        public UnarmedWeaponController(WeaponRuntimeState state, IWeaponStatSource statSource = null)
        {
            State       = state;
            _def        = state.Def;
            _statSource = statSource;
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
            _frameAccum += dt * SimClock.FramesPerSecond;
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
            bool isAuto = _def.IsAuto;
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
            // AS3 stamps the two hit procs on the bullet at fire time; WKick/WPunch go through
            // Weapon.shoot like any other weapon, so an unarmed strike carries the owner's
            // critInvis/desintegr exactly as a rifle does (Weapon.as:1697/:1525-1527).
            DamageContext baseDmg = DamageContext.FromWeapon(
                _def, null, State.OwnerFaction,
                critInvisChance: _statSource != null ? _statSource.CritInvis : 0f,
                desintegrChance: _statSource != null ? _statSource.Desintegr : 0f);

            // AS3 WKick.as:47-48 — punchDamMult scales BOTH the damage and the knockback:
            //   b.damage = damage * punchDamMult;  b.otbros = otbros * punchDamMult;
            // (Unlike melee, where the knock has no stat term at all — WClub.as:594.)
            //
            // AS3 also raises the stun proc when punchDamMult > 1 (WKick.as:52-54:
            // dopCh = punchDamMult - 1, dopDamage = 30). That is NOT wired: DopChance and DopDamage
            // have no consumer anywhere in the port, so writing them would be an inert fix. Recorded
            // in the class remark; do not add it here until a stun resolver reads them.
            float punchMult = PunchDamMult;

            // AS3: b.damage = damage*2; b.otbros *= 1.5 on a back-hit (zadok).
            float damageScale    = (zadok ? 2f : 1f) * punchMult;
            float knockbackScale = (zadok ? 1.5f : 1f) * punchMult;

            // Scaled through one method rather than a positional re-construction: the 16-argument
            // copy this replaced silently dropped ownerFaction, and would drop every field added to
            // DamageContext afterwards — including the hit-avoidance terms.
            DamageContext dmgCtx = baseDmg.WithScaledDamage(
                damageScale,
                knockbackScale,
                new Vector2(Mathf.Cos(punchAngle), Mathf.Sin(punchAngle)));

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
                cues:          cues,
                meleePrevTip:  holdPos,
                meleeCurrTip:  impactPos
            ));

            State.IsShoot = true;
        }
    }
}
