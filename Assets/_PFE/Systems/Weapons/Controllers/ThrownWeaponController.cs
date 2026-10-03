using System.Collections.Generic;
using UnityEngine;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Systems.Combat;
using PFE.Systems.Inventory;

namespace PFE.Systems.Weapons.Controllers
{
    /// <summary>
    /// AS3 <c>WThrow.as</c> — the <c>tip == 4</c> thrown-weapon family.
    ///
    /// Two sub-behaviours driven by <see cref="WeaponDefinition.throwTip"/>:
    ///   0 / 2 — arc throw: initial velocity from cursor distance + weapon skill, emitted as
    ///           <see cref="ShotKind.ThrownObject"/>. <c>throwTip == 2</c> additionally latches on
    ///           first tile contact (<c>lip</c>) instead of bouncing.
    ///   1     — mine placement: <see cref="ShotKind.Mine"/> at the thrower's position, arming for
    ///           <c>reloadTime = 75</c> frames (<c>WThrow.as:157</c>).
    ///
    /// Radio detonation (<see cref="WeaponDefinition.radio"/>) lives on <see cref="StartReload"/>, not
    /// on the attack button: AS3 calls <c>detonator()</c> from the RELOAD key
    /// (<c>UnitPlayer.as:2358</c>). That is not a style choice — <c>x37</c> is a mine with
    /// <c>sens='0'</c>, i.e. it never triggers on proximity, so the detonator is its only way to fire.
    ///
    /// <para><b>Deliberately not ported here, and where it lives instead.</b> AS3's <c>WThrow.attack()</c>
    /// opens with its own weapon-skill gate (<c>:78-106</c>: <c>skillConf</c> 0.75/0.5 on a one- or
    /// two-tier deficit, a refusal beyond that, plus the <c>lvlNoUse</c> branch for mines and sticky
    /// bombs). That is the thrown half of the weapon-skill workstream, which also owns
    /// <c>skillPlusDam</c> and the per-type damage shapes — see <c>docs/RPG_Skill_Fix_Plan.md</c>.
    /// Porting the refusal without the 0.75/0.5 penalty would be half a rule, so the whole block waits
    /// for that plan rather than being split across two changes. Consequently <c>_loc1_</c> here is the
    /// owner's live skill multiplier where the oracle reads one, but <c>skillConf</c> is 1 and no damage
    /// term is skill-scaled.</para>
    /// </summary>
    public sealed class ThrownWeaponController : IWeaponController
    {
        // ── Constants ─────────────────────────────────────────────────────────

        private const float PpuScale = 100f;

        // ── State ─────────────────────────────────────────────────────────────

        public WeaponRuntimeState State { get; }
        private readonly WeaponDefinition _def;

        /// <summary>
        /// The owner's RPG multipliers — null means "no owner stats", in which case every accessor
        /// falls back to the AS3 declaration default. Same contract as
        /// <see cref="RangedWeaponController"/>'s source, and read live so a perk taken mid-fight
        /// applies to the next throw.
        /// </summary>
        private readonly IWeaponStatSource _statSource;

        /// <summary>
        /// Turns the live ammo id into its ballistics row. AS3's <c>WThrow.shoot()</c> calls
        /// <c>setBullet(b)</c> (<c>WThrow.as:191</c>), which folds the round's damage/pier/armour/
        /// knock/prec/probiv onto the shot — so a thrown weapon's round matters exactly as much as a
        /// gun's. Null (tests, headless rigs) is AS3's "неправильный патрон" branch: every multiplier
        /// stays at its identity.
        /// </summary>
        private readonly IAmmoResolver _ammoResolver;

        private readonly PFE.Core.Rng.IRngService _rng;

        private float   _frameAccum;
        private bool    _attackHeld;
        private Vector2 _lastAimTarget;

        // Simplified ammo counter (AS3 kolAmmo, default 4) — the NPC half of WThrow.getAmmo()
        // (WThrow.as:274-279). AS3 gives the PLAYER the inventory half instead
        // (`getInvAmmo(ammo,1,1,true)`), which the port does not model yet; using this counter for
        // both is the recorded divergence, not a new one.
        private int _kolAmmo;
        private const int DefaultKolAmmo = 4;

        /// <summary>
        /// AS3 <c>_loc1_</c> — the weapon-skill multiplier WThrow reads in <c>shoot()</c>
        /// (<c>WThrow.as:130-138</c>) and again in <c>setTrass</c>. It scales the launch speed, the
        /// distance term and the deviation divisor, so it is a live read rather than a snapshot.
        /// 1 is the oracle's own value for a unit with no <c>Pers</c>.
        /// </summary>
        private float WeaponSkill => _statSource != null
            ? _statSource.WeaponSkillMultiplier(_def.skillLevel)
            : 1f;

        /// <summary>
        /// AS3 <c>owner.mazil</c> (<c>WThrow.as:139</c>) — the muzzle inaccuracy addend, in the
        /// deviation model's own units. For the player this is <c>Pers.mazilAdd</c>
        /// (<c>UnitPlayer.as:1182</c>), which is what <see cref="IWeaponStatSource.MazilAdd"/> carries;
        /// enemy units set <c>mazil</c> to a hardcoded AI value (5/16/25) in their own controllers,
        /// which the port does not model — 0 is that gap, recorded rather than invented.
        /// </summary>
        private float Mazil => _statSource != null ? _statSource.MazilAdd : 0f;

        private readonly List<ShotPlan> _plans = new();

        // ── Constructor ───────────────────────────────────────────────────────

        public ThrownWeaponController(WeaponRuntimeState state, IWeaponStatSource statSource = null,
                                      IAmmoResolver ammoResolver = null,
                                      PFE.Core.Rng.IRngService rng = null)
        {
            State         = state;
            _def          = state.Def;
            _statSource   = statSource;
            _ammoResolver = ammoResolver;
            _rng          = rng != null
                ? rng.GetStream(PFE.Core.Rng.RngStream.Combat)
                : new PFE.Core.Rng.PcgRngService().GetStream(PFE.Core.Rng.RngStream.Combat);
            _kolAmmo      = _def.magazineSize > 0 ? _def.magazineSize : DefaultKolAmmo;
        }

        // ── IWeaponController ─────────────────────────────────────────────────

        public void BeginAttack()
        {
            _attackHeld = true;
        }

        public void EndAttack() => _attackHeld = false;

        /// <summary>
        /// The reload key. For a radio weapon this is the detonator, not a reload — AS3's
        /// <c>UnitPlayer.as:2358</c> calls <c>currentWeapon.detonator()</c> from <c>keyReload</c>, and
        /// <c>WThrow.detonator()</c> (<c>:297-312</c>) activates every placed mine whose id matches.
        ///
        /// <para>The key is consumed: AS3 clears <c>ctr.keyReload</c> when <c>detonator()</c> returns
        /// true, so one press is one detonation. The port models that by emitting only on the
        /// <c>StartReload</c> edge — the input layer calls this on key-down, not every frame.</para>
        ///
        /// <para>A non-radio thrown weapon reloads nothing (<c>WThrow.reloadWeapon()</c> is empty), so
        /// this is a no-op for it.</para>
        /// </summary>
        public void StartReload()
        {
            if (!_def.radio) return;
            EmitDetonation();
        }

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
        /// Mirrors the tail of <c>WThrow.attack()</c> (<c>WThrow.as:107-114</c>).
        /// </summary>
        private void RunAttack()
        {
            if (State.TAttack > 0) return;

            State.IsAttack = true;

            // Single-shot debounce (non-auto thrown weapons). AS3: `!param1 && !auto && t_auto > 0`
            // → t_auto = 3, no shot. The `param1` half is the "called from a scripted path" flag,
            // which the port has no equivalent of.
            bool isAuto = _def.IsAuto;
            if (!isAuto && State.TAuto > 0)
            {
                State.TAuto = 3;
                return;
            }

            // AS3 arms t_attack only when the ammo check passes; a failure leaves the weapon silent
            // rather than refusing outright.
            if (!ConsumeAmmo()) return;

            State.TAttack = Mathf.RoundToInt(_def.rapid);
        }

        private void RunActions()
        {
            // Shoot trigger at t_attack == rapid (single pulse, not burst). AS3 `Weapon.as:1183`:
            // `if(this.dkol <= 0 && this.t_attack == this.rapid) this.shoot();`
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
        /// Mirrors <c>WThrow.shoot()</c> (<c>WThrow.as:127-220</c>).
        /// Mine placement (<c>throwTip == 1</c>) or arc throw (everything else).
        /// </summary>
        private void Shoot()
        {
            // ── The round this throw consumes ─────────────────────────────────
            // WThrow.shoot() calls setBullet(b) at :191, so the ammo's six fire-time terms reach the
            // shot. Read from ResolvedAmmoType so a debug ammo swap actually changes the throw.
            AmmoDefinition ammo = _ammoResolver?.Resolve(State.ResolvedAmmoType);

            // skillConf is AS3's level-deficit penalty, set by WThrow.attack()'s own gate (:78-106).
            // 1 until that gate is ported — see the class note.
            const float skillConf = 1f;

            DamageContext damCtx = DamageContext.FromWeapon(
                _def, null, State.OwnerFaction,
                // No ownerWeaponSkillLevel / weaponSkillMultiplier: WThrow's damage shape is NOT the
                // base one. It inlines `(damage + damAdd) * damMult * (1 + (_loc1_ - 1) * 0.5)`
                // (:185-186) — half the skill bonus the base resultDamage() applies, and without
                // skillPlusDam or the `(1 - breaking*0.3)` wear term. Encoding that per-class shape
                // belongs to the weapon-skill plan, not to a shared factory, so the shot is built
                // unscaled here and the gap is recorded in
                // docs/AUDIT_throwable_and_explosive_2026-10-03.md.
                ammo: ammo);

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
                // ── Mine placement (WThrow.as:142-174) ─────────────────────────
                // AS3 places the mine at the weapon's own (X, Y), arms it for reloadTime = 75 frames
                // (:157) and starts its countdown at `explTime * 0.3` (:155). The countdown source is
                // the SAME <char time> the fuse uses, which is why plan.FuseFrames is right for both.
                //
                // Not modelled here: the collision retry that drops the mine onto the thrower
                // (`setPos(owner.X, owner.Y)`) and then freezes it (`fixed = true`), and the
                // `pers.sapper` perk's damage bonus plus its EXPL/PLASMA immunity (:169-173).
                _plans.Add(new ShotPlan(
                    origin:        ShotOrigin.HoldPoint,
                    worldPosition: spawnPos,
                    angleRad:      0f,
                    kind:          ShotKind.Mine,
                    damage:        damCtx,
                    pelletIndex:   0,
                    totalPellets:  1,
                    cues:          cues,
                    fuseFrames:    _def.fuseFrames,
                    isMine:        true));
            }
            else
            {
                // ── Arc throw (WThrow.as:175-205) ──────────────────────────────
                float throwVelPx = GetVel(spawnPos, _lastAimTarget, WeaponSkill * skillConf);

                // AS3: `b.rot = rot + _loc2_ + getRot(_loc3_, _loc4_, b.vel)` (:180), where _loc2_ is
                // the random deviation below and getRot compensates for the launch arc. Note the
                // order — getRot reads the ALREADY-computed b.vel, not the raw speed.
                float angle = State.Rot
                            + CalculateDeviation()
                            + GetRot(spawnPos, _lastAimTarget, throwVelPx);

                // px/frame → units/s. The one conversion, in one place.
                float throwVel = throwVelPx / PpuScale * SimClock.FramesPerSecond;

                _plans.Add(new ShotPlan(
                    origin:        ShotOrigin.ThrowPoint,
                    worldPosition: spawnPos,
                    angleRad:      angle,
                    kind:          ShotKind.ThrownObject,
                    damage:        damCtx,
                    pelletIndex:   0,
                    totalPellets:  1,
                    cues:          cues,
                    throwVelocity: new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * throwVel,
                    fuseFrames:    _def.fuseFrames,
                    isMine:        false,
                    // throwTip == 2 sets `lip` (WThrow.as:193): the object latches on its first tile
                    // contact instead of bouncing.
                    sticky:        _def.throwTip == 2));
            }

            // ── Post-shot state (WThrow.as:206-218) ────────────────────────────
            // NOTE: no durability charge. WThrow.shoot() OVERRIDES the base shoot() and never calls
            // super, and the only `hp -=` in Weapon.as is :1598 INSIDE that base method — so a thrown
            // weapon never wears out in AS3. The port used to subtract 1 per throw, which is an
            // invention; it is gone.
            //
            // NOTE: no `t_shoot` either. The port used to set `TShoot = 3` here, but AS3 never arms
            // it for a thrown weapon, on two independent grounds:
            //   1. WThrow's constructor sets `animated = false` outright (WThrow.as:39) — not
            //      conditionally like WMagic — and `t_shoot = 3` lives behind
            //      `if(this.animated && this.t_shoot <= 1)` (Weapon.as:1600).
            //   2. That line is inside `Weapon.shoot()`, and `WThrow.shoot()` (WThrow.as:127-220)
            //      never calls `super.shoot()` — its only `super` calls are the constructor,
            //      animate() and setNull().
            // So `t_shoot` stays 0 for the whole life of a thrown weapon. Arming it made the
            // presenter's shoot branch (which runs before its prep branch and needs
            // `shootFrameStart >= 0`) a candidate for a weapon whose vis is a single held frame.
            // AS3 keeps WThrow's vis on frame 1: `vis.gotoAndStop(1)` in the constructor.
            State.TAuto   = 3;
            State.IsShoot = true;
        }

        /// <summary>
        /// Emit a radio detonation signal.
        /// ProjectileSpawner detects FuseFrames==0 &amp;&amp; IsMine==false and calls
        /// <c>MineObject.DetonateAll</c>.
        /// AS3: <c>WThrow.detonator()</c> iterates <c>loc.units</c> and calls <c>activate()</c> on
        /// every <c>Mine</c> whose id matches.
        /// </summary>
        private void EmitDetonation()
        {
            _plans.Add(new ShotPlan(
                origin:        ShotOrigin.HoldPoint,
                worldPosition: new Vector2(State.X, State.Y),
                angleRad:      0f,
                kind:          ShotKind.Mine,
                damage:        DamageContext.FromWeapon(_def, null, State.OwnerFaction),
                pelletIndex:   0,
                totalPellets:  1,
                cues:          ShotCues.None,
                fuseFrames:    0,      // 0 = detonation signal, not new placement
                isMine:        false));
        }

        // ── Throw velocity (WThrow.getVel / getRot) ────────────────────────────

        /// <summary>
        /// AS3 <c>WThrow.getVel()</c> (<c>WThrow.as:117-120</c>):
        /// <code>
        /// Math.min(speed * param3,
        ///          Math.sqrt(param1*param1 + param2*param2) / 10 * param3 - param2 / 10 * param3)
        /// </code>
        /// where <c>param1</c>/<c>param2</c> are the cursor offset in <b>Flash pixels</b> and
        /// <c>param3</c> is <c>weaponSkill * skillConf</c>.
        ///
        /// <para><b>There is no lower clamp, and adding one changes the game.</b> The distance term
        /// goes negative whenever the cursor sits above the thrower by more than the geometry allows
        /// for — a steep upward lob — and the oracle lets that negative through, which is what makes
        /// such a throw drop short. The port used to wrap the result in <c>Mathf.Max(1f, …)</c>,
        /// silently turning every one of those into a 1 px/frame nudge. Removed.</para>
        /// </summary>
        private float GetVel(Vector2 spawnPos, Vector2 aimTarget, float skill)
        {
            float dXpx = (aimTarget.x - spawnPos.x) * PpuScale;
            float dYpx = (aimTarget.y - spawnPos.y) * PpuScale;

            float distTerm = Mathf.Sqrt(dXpx * dXpx + dYpx * dYpx) / 10f * skill
                           - dYpx / 10f * skill;

            return Mathf.Min(_def.projectileSpeed * skill, distTerm);
        }

        /// <summary>
        /// AS3 <c>WThrow.getRot()</c> (<c>WThrow.as:122-125</c>):
        /// <c>-param1 / (param3 + 0.0001) / 100</c> — the launch-angle correction that makes a flat
        /// throw arc. <c>param3</c> is the velocity <see cref="GetVel"/> just returned, in px/frame.
        /// </summary>
        private float GetRot(Vector2 spawnPos, Vector2 aimTarget, float velPx)
        {
            float dXpx = (aimTarget.x - spawnPos.x) * PpuScale;
            return -dXpx / (velPx + 0.0001f) / 100f;
        }

        /// <summary>
        /// AS3 <c>WThrow.shoot():139</c>:
        /// <code>
        /// (Math.random() - 0.5) * (deviation / (_loc1_ + 0.01) + owner.mazil) * 3.1415 / 180
        /// </code>
        ///
        /// <para><b>This was entirely absent</b> — every throw in the port flew at the exact aim
        /// angle. Note the shape differs from the ranged twin (<c>Weapon.as:1460</c>): WThrow has no
        /// <c>breaking</c> term, no <c>skillConf</c> divisor and no <c>devMult</c>. Only <c>mercgr</c>
        /// and <c>drongr</c> carry a non-zero <c>phis@deviation</c> (5), so this is small in magnitude
        /// but it is the oracle's whole spread model for the family.</para>
        ///
        /// <para>The <c>3.1415 / 180</c> is the oracle's own literal, not a rounded
        /// <c>Mathf.Deg2Rad</c> — reproduced so the angles match.</para>
        /// </summary>
        private float CalculateDeviation()
        {
            float spread = _def.deviation / (WeaponSkill + 0.01f) + Mazil;
            return (_rng.NextFloat() - 0.5f) * spread * 3.1415f / 180f;
        }

        // ── Ammo ──────────────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>WThrow.getAmmo()</c> (<c>WThrow.as:268-280</c>). The player half reads the
        /// inventory; the NPC half is this counter, decremented on each call. The port uses the NPC
        /// half for everyone — recorded, not new.
        /// </summary>
        private bool ConsumeAmmo()
        {
            if (_kolAmmo <= 0) return false;
            _kolAmmo--;
            return true;
        }
    }
}
