using System.Collections.Generic;
using UnityEngine;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Systems.Combat;
using PFE.Systems.Inventory;

namespace PFE.Systems.Weapons.Controllers
{
    /// <summary>
    /// Full AS3-parity implementation of Weapon.as for tip 0/2/3 ranged weapons.
    ///
    /// Mirrors three key methods from AS3:
    ///   attack()  — called when fire input is held; manages prep charge and sets t_attack
    ///   actions() — runs every flash frame; advances all timers, fires shoot() when due
    ///   shoot()   — builds ShotPlan(s) for this shot (pellets, deviation, recoil)
    ///
    /// Frame cadence: AS3 runs at 30fps. We accumulate Unity dt into flash frames so
    /// the timer values match the original regardless of Unity's FixedUpdate rate.
    ///
    /// Position: lerps toward holdPoint at 1/5 per frame (AS3: X += (weaponX-X)/5).
    /// Rotation: smoothly closes on aim angle at drot radians/frame (State.Ready when aligned).
    /// </summary>
    public sealed class RangedWeaponController : IWeaponController
    {
        // ── Constants ─────────────────────────────────────────────────────────

        private const float PpuScale   = 100f;   // Flash pixels → Unity units

        // ── State ─────────────────────────────────────────────────────────────

        public WeaponRuntimeState State { get; }
        private readonly WeaponDefinition _def;
        private readonly PfeDebugSettings _debugSettings;

        // Flash-frame accumulator — fractional frames carry over between Tick() calls.
        private float _frameAccum;

        // Input state latched per Tick (set by BeginAttack/EndAttack before Tick).
        private bool _attackHeld;
        private bool _attackJustPressed; // edge: fire button pressed this tick

        // Previous aim point for rot2 calculation (needed for ready check).
        private Vector2 _lastAimTarget;

        // Ammo source (player inventory). Null = training/infinite mode.
        private readonly IAmmoSource _ammoSource;

        /// <summary>
        /// The owner's RPG multipliers. Null means "no owner stats" — see
        /// <see cref="IWeaponStatSource"/> — in which case every accessor below falls back to the
        /// AS3 <c>Pers</c> declaration default rather than to 0.
        /// </summary>
        private readonly IWeaponStatSource _statSource;

        /// <summary>
        /// Turns the weapon's live ammo <b>id</b> into its ballistics row — the port's stand-in for
        /// AS3's <c>World.w.invent.items[this.ammo].xml</c> lookup inside <c>Weapon.setAmmo</c>
        /// (<c>Weapon.as:1746-1809</c>). Null (tests, headless loadouts) means every shot resolves no
        /// round, which is AS3's "неправильный патрон" branch: every ammo multiplier stays at its
        /// identity and the round contributes nothing. See <see cref="IAmmoResolver"/>.
        /// </summary>
        private readonly IAmmoResolver _ammoResolver;

        // AS3 copies these four onto the weapon in setPers (Weapon.as:973-977) and the weapon then
        // reads its own copy. The port reads the live source instead, so a perk taken mid-fight
        // applies to the next shot without re-equipping.
        private float ReloadMult => _statSource != null ? _statSource.ReloadMult : 1f;
        private float RecoilMult => _statSource != null ? _statSource.RecoilMult : 1f;
        private float JammedMult => _statSource != null ? _statSource.JammedMult : 1f;

        /// <summary>
        /// The owner's weapon-skill <b>tier</b> for this weapon's skill code — AS3
        /// <c>pers.getWeapLevel(this.skill)</c> (<c>Pers.as:1073-1104</c>, a 0..5 tier, not a point
        /// total). <see cref="HitAvoidance.UnknownOwnerSkillLevel"/> when there is no stat source.
        ///
        /// <para>Both consumers read it: <see cref="CheckAvail"/> measures the gap that can refuse the
        /// shot, and <c>Shoot()</c> hands it to <see cref="DamageContext.FromWeapon"/> for the miss
        /// term. Deriving it twice would be two copies of the same rule — the shape of bug this
        /// change is closing.</para>
        /// </summary>
        private int OwnerWeaponSkillLevel => _statSource != null
            ? _statSource.OwnerWeaponSkillLevel(_def.skillLevel)
            : HitAvoidance.UnknownOwnerSkillLevel;

        /// <summary>
        /// The owner's weapon-skill <b>multiplier</b> for this weapon's skill code — AS3 <c>_loc1_</c>
        /// in <c>Weapon.shoot</c> (<c>Weapon.as:1451-1459</c>), <c>Pers.weaponSkills[skill]</c>, read
        /// through <c>IWeaponStatSource.WeaponSkillMultiplier</c>. <c>1</c> (the identity) when there is
        /// no stat source.
        ///
        /// <para>AS3 reads it twice per shot: as <c>p2</c> of <c>resultDamage</c> (folded into damage by
        /// <c>DamageContext.FromWeapon</c>) and as the spread divisor <c>(_loc1_ + 0.01)</c> in
        /// <c>shoot</c>'s deviation term (<c>:1460</c>). One accessor, so the two cannot disagree about
        /// which code they asked about.</para>
        /// </summary>
        private float WeaponSkillMultiplier => _statSource != null
            ? _statSource.WeaponSkillMultiplier(_def.skillLevel)
            : 1f;

        // Total frames at reload start — used to compute ReloadProgressRP each tick.
        private int _reloadTotal;

        // Shot plan accumulator — filled during flash frames, flushed by FlushShotPlans.
        private readonly List<ShotPlan> _plans = new();
        private readonly PFE.Core.Rng.IRngService _rng;

        /// <summary>
        /// World-space barrel tip, pushed in by <see cref="PlayerWeaponLoadout"/> from the
        /// <see cref="WeaponPresenter"/> that actually draws the gun. Null = the vis has not been
        /// drawn yet (or there is no presenter), and the shot falls back to the weapon's own
        /// position — which is what AS3 does when <c>vis.emit</c> is missing.
        ///
        /// <para><b>Why the presenter owns this and not the controller.</b> The muzzle has to go
        /// through the vis's flip and rotation, and only the presenter knows the vis transform it
        /// just wrote. Re-deriving the same transform here would be a second copy of the flip rule
        /// — the exact shape of bug this change is fixing.</para>
        /// </summary>
        public Vector2? MuzzleWorldPoint { get; set; }

        // ── Constructor ───────────────────────────────────────────────────────

        public RangedWeaponController(WeaponRuntimeState state, PfeDebugSettings debugSettings = null,
                                      IAmmoSource ammoSource = null, PFE.Core.Rng.IRngService rng = null,
                                      IWeaponStatSource statSource = null, IAmmoResolver ammoResolver = null)
        {
            State          = state;
            _def           = state.Def;
            _debugSettings = debugSettings;
            _ammoSource    = ammoSource;
            _statSource    = statSource;
            _ammoResolver  = ammoResolver;
            _rng           = rng != null ? rng.GetStream(PFE.Core.Rng.RngStream.Combat) : new PFE.Core.Rng.PcgRngService().GetStream(PFE.Core.Rng.RngStream.Combat);

            // Weapons with rechargeFrames start with a full magazine.
            if (_def.rechargeFrames > 0)
                State.CurrentAmmo = _def.magazineSize;

            State.TRech = _def.rechargeFrames;
        }

        // ── IWeaponController ─────────────────────────────────────────────────

        public void BeginAttack()
        {
            _attackHeld        = true;
            _attackJustPressed = true;

            if (_debugSettings?.LogWeaponControllerDiagnostics == true)
                Debug.Log($"[RangedWeaponController] BeginAttack weapon='{_def.weaponId}' ammo={State.CurrentAmmo}.");
        }

        public void EndAttack()
        {
            _attackHeld = false;

            if (_debugSettings?.LogWeaponControllerDiagnostics == true)
                Debug.Log($"[RangedWeaponController] EndAttack weapon='{_def.weaponId}' ammo={State.CurrentAmmo}.");
        }

        public void StartReload()
        {
            if (State.TReload <= 0 && State.CurrentAmmo < _def.magazineSize && !State.NeedsReload)
                InitReload();
        }

        public void Tick(float dt, Vector2 holdPoint, Vector2 hornPoint, Vector2 aimTarget)
        {
            _lastAimTarget = aimTarget;

            // Accumulate flash frames.
            _frameAccum += dt * SimClock.FramesPerSecond;
            int frames = Mathf.FloorToInt(_frameAccum);
            _frameAccum -= frames;

            if (_debugSettings?.LogWeaponControllerDiagnostics == true &&
                (_attackJustPressed || _attackHeld || State.TAttack > 0 || State.TReload > 0 || State.TPrep > 0))
            {
                Debug.Log(
                    $"[RangedWeaponController] Tick weapon='{_def.weaponId}' dt={dt:0.###} frames={frames} accum={_frameAccum:0.###} " +
                    $"attackHeld={_attackHeld} justPressed={_attackJustPressed} ammo={State.CurrentAmmo} " +
                    $"tAttack={State.TAttack} tReload={State.TReload} tPrep={State.TPrep}.");
            }

            for (int i = 0; i < frames; i++)
                TickOneFlashFrame(holdPoint, aimTarget);

            // Reset per-Tick edge flags.
            _attackJustPressed = false;

            // Sync reactive props to current state.
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

        // ── Core per-flash-frame logic ────────────────────────────────────────

        /// <summary>
        /// One 30fps flash frame. Mirrors Weapon.actions() + Weapon.attack() combined.
        /// attack() is called first (if input held), then actions() runs unconditionally.
        /// </summary>
        private void TickOneFlashFrame(Vector2 holdPoint, Vector2 aimTarget)
        {
            // ── Position lerp (AS3: X += (owner.weaponX - X) / 5) ────────────
            // On first frame (X==0, Y==0) snap instantly.
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

            // ── Rotation smoothing (AS3: drot smoothing toward rot2) ──────────
            float rot2 = Mathf.Atan2(aimTarget.y - State.Y, aimTarget.x - State.X);
            AdvanceRotation(rot2);

            // ── Input: attack() ───────────────────────────────────────────────
            if (_attackHeld)
                RunAttack();

            // ── actions() timers and shoot triggers ────────────────────────────
            RunActions();

            // Clear per-frame shoot flag after actions.
            State.IsShoot = false;
        }

        /// <summary>
        /// Mirrors Weapon.attack() — manages prep and triggers t_attack.
        /// </summary>
        private void RunAttack()
        {
            // Broken weapon.
            if (State.IsBroken) return;

            // Single-shot debounce (auto=false && t_auto > 0 → increment pow, skip fire).
            bool isAuto = _def.IsAuto;
            if (!isAuto && State.TAuto > 0)
            {
                State.TAuto = 3; // refresh debounce window
                State.Pow++;
                return;
            }

            // ── Skill gate: AS3 checkAvail() (Weapon.as:1304-1310, :1366-1388) ─────────────
            // Sits here, between the debounce and the ammo check, because that is where the oracle
            // puts it (:1306 before :1311) — an under-skilled owner with an empty magazine gets the
            // refusal, not a reload. Refusing here means t_attack is never armed, so Shoot() is never
            // reached and the round is never built: exactly AS3's "attack() returns false".
            //
            // Gated on the stat source, which is the port's stand-in for AS3's `if(this.owner.player)`.
            // PlayerWeaponLoadout is the only production path that supplies one (it reads the owner's
            // CharacterStats), so an NPC weapon — and every training rig — skips the gate as AS3 does.
            if (!CheckAvail())
            {
                if (_debugSettings?.LogWeaponControllerDiagnostics == true)
                    Debug.Log($"[RangedWeaponController] RunAttack weapon='{_def.weaponId}' refused: " +
                              $"requiredLevel={_def.weaponLevel} ownerSkillTier={OwnerWeaponSkillLevel}.");
                return;
            }

            // If magazine-fed and empty — trigger reload.
            if (_def.magazineSize > 0 && State.CurrentAmmo < _def.ammoPerShot)
            {
                if (_debugSettings?.LogWeaponControllerDiagnostics == true)
                    Debug.Log($"[RangedWeaponController] RunAttack weapon='{_def.weaponId}' blocked by ammo. CurrentAmmo={State.CurrentAmmo}, ammoPerShot={_def.ammoPerShot}.");
                InitReload();
                return;
            }

            // Jammed — handled inside weaponAttack/shoot, not here.
            // (AS3 calls weaponAttack() which checks jammed first.)

            State.IsAttack = true;

            // Prep charge (minigun, railgun, etc.).
            if (State.TPrep < _def.prepFrames + 10)
                State.TPrep += 2;

            // Once prep is satisfied and no pending attack/reload, arm t_attack.
            if (State.TPrep >= _def.prepFrames && State.TAttack <= 0 && State.TReload <= 0)
            {
                int burstFrames = _def.burstCount <= 0
                    ? Mathf.RoundToInt(_def.rapid)
                    : Mathf.RoundToInt(_def.rapid) * (_def.burstCount + 1);

                State.TAttack = burstFrames;

                if (_debugSettings?.LogWeaponControllerDiagnostics == true)
                {
                    Debug.Log(
                        $"[RangedWeaponController] RunAttack armed tAttack={State.TAttack} weapon='{_def.weaponId}' " +
                        $"prep={State.TPrep}/{_def.prepFrames} burstCount={_def.burstCount}.");
                }

                // Single-shot weapons reload immediately after firing.
                if (_def.magazineSize == 1)
                    InitReload();
            }
        }

        /// <summary>
        /// AS3 <c>Weapon.checkAvail()</c> (<c>Weapon.as:1366-1388</c>) — may this owner fire?
        ///
        /// <para>The oracle splits into three answers on <c>gap = this.lvl - pers.getWeapLevel(skill)</c>:
        /// gap 1 → <c>skillConf 0.8</c>, gap 2 → <c>0.6</c>, gap &gt; 2 → <b>refuse</b>. The first two are
        /// penalties, not refusals, and the port already applies them where the oracle does — as the
        /// round's miss term, through <see cref="HitAvoidance.MissChance"/> inside
        /// <see cref="DamageContext.FromWeapon"/>. Only the third is a gate, so only the third is here.
        /// Reading both halves out of one place keeps the 0.8/0.6 constants single-sourced.</para>
        ///
        /// <para><b>Scope.</b> This is the <i>base</i> <c>attack()</c> path — the ranged types this
        /// controller serves. <c>WMagic</c> also reaches <c>checkAvail()</c> and will need the same
        /// call; <c>WThrow</c> has its own 0.75/0.5 copy; <c>WKick</c>/<c>WPunch</c>/<c>WPaint</c> have
        /// no gate. See <see cref="HitAvoidance.CanFire"/>.</para>
        ///
        /// <para><b>Divergence, behaviour-equivalent (recorded, not ported).</b> The oracle's caller
        /// acts on the <c>false</c>: <c>UnitPlayer.as:2342-2345</c> does
        /// <c>if(!currentWeapon.attack()) this.ctr.keyAttack = false;</c> — a refused shot releases the
        /// trigger latch. The port has no <c>keyAttack</c>; the trigger lives in the input layer and
        /// this controller only sees <c>_attackHeld</c>. The outcome is the same because the gate is a
        /// deterministic function of the weapon's requirement and the owner's tier, neither of which
        /// changes while the trigger is held: every subsequent frame re-runs <c>RunAttack</c>, refuses
        /// again, and returns before <c>State.IsAttack</c> and the prep charge — the two things
        /// <c>weaponAttack()</c> would have touched. A held trigger on a refused weapon therefore
        /// behaves identically whether or not the latch is cleared.</para>
        /// </summary>
        private bool CheckAvail() => HitAvoidance.CanFire(_def.weaponLevel, OwnerWeaponSkillLevel);

        /// <summary>
        /// Mirrors the timer-management + shoot-trigger block of Weapon.actions().
        /// </summary>
        private void RunActions()
        {
            // ── Shoot trigger ─────────────────────────────────────────────────
            // Single fire: shoot when t_attack == rapid.
            // Burst fire:  shoot when t_attack > rapid AND t_attack % rapid == 0.
            if (State.TAttack > 0)
            {
                bool isBurst = _def.burstCount > 0;
                if (!isBurst && State.TAttack == _def.rapid)
                {
                    if (_debugSettings?.LogWeaponControllerDiagnostics == true)
                        Debug.Log($"[RangedWeaponController] RunActions triggering Shoot() weapon='{_def.weaponId}' tAttack={State.TAttack}.");
                    Shoot();
                }
                else if (isBurst && State.TAttack > _def.rapid && State.TAttack % _def.rapid == 0)
                {
                    if (_debugSettings?.LogWeaponControllerDiagnostics == true)
                        Debug.Log($"[RangedWeaponController] RunActions triggering burst Shoot() weapon='{_def.weaponId}' tAttack={State.TAttack}.");
                    Shoot();
                }
            }

            // ── Countdown timers ──────────────────────────────────────────────
            if (State.TAttack > 0) State.TAttack--;
            if (State.TRel   > 0) State.TRel--;
            if (State.TRet   > 0) State.TRet--;
            if (State.TShoot > 0) State.TShoot--;

            // ── RotUp decay (AS3: rotUp *= 0.9 if > 5, else -= 0.5, else 0) ───
            if (State.RotUp > 5f)       State.RotUp *= 0.9f;
            else if (State.RotUp > 0.5f) State.RotUp -= 0.5f;
            else                          State.RotUp  = 0f;

            // ── Prep decay (decrement when not firing) ─────────────────────────
            if (State.TPrep > 0) State.TPrep--;
            else                 State.KolShoot = 0;

            // ── Auto debounce timer ───────────────────────────────────────────
            if (State.TAuto > 0) State.TAuto--;
            else                 State.Pow = 0;

            // ── Self-recharge (recharg weapons) ───────────────────────────────
            if (_def.rechargeFrames > 0 && State.CurrentAmmo < _def.magazineSize && State.TAttack == 0)
            {
                State.TRech--;
                if (State.TRech <= 0)
                {
                    State.CurrentAmmo++;
                    State.TRech = _def.rechargeFrames;
                }
            }

            // ── Reload countdown ──────────────────────────────────────────────
            if (State.TAttack == 0 && State.TReload > 0)
            {
                State.TReload--;
                // Update progress 0→1 over the reload duration so WeaponPresenter
                // can map to the correct reload animation frame.
                if (_reloadTotal > 0)
                    State.ReloadProgressRP.Value = 1f - (float)State.TReload / _reloadTotal;
            }

            // Reload completes at 10 frames remaining (AS3: t_reload == round(10 * reloadMult)).
            if (State.TReload == Mathf.RoundToInt(10 * ReloadMult))
                CompleteReload();

            // ── Reset input flag at end of frame ─────────────────────────────
            State.WasAttack = State.IsAttack;
            State.IsAttack  = false;
        }

        /// <summary>
        /// Mirrors Weapon.shoot() — jam check, pellet loop, emit ShotPlan(s).
        /// </summary>
        private void Shoot()
        {
            if (_debugSettings?.LogWeaponControllerDiagnostics == true)
            {
                Debug.Log(
                    $"[RangedWeaponController] Shoot() entered weapon='{_def.weaponId}' ammo={State.CurrentAmmo} " +
                    $"durability={State.CurrentDurability} rot={State.Rot:0.###} aim={_lastAimTarget}.");
            }

            // ── Jam / misfire check ───────────────────────────────────────────
            // AS3 (Weapon.as:1426-1446) guards this whole block with `this.owner && this.owner.player`,
            // so enemy weapons never jam. The port applies it to every owner, which is a real
            // divergence — but it is not one of the statId gaps this change is closing, and gating it
            // here would need the test rig to grow a stat source, so it is recorded rather than fixed.
            float breaking = State.Breaking();
            if (breaking > 0f)
            {
                float rnd = _rng.NextFloat();
                int   holderSafe = Mathf.Max(20, _def.magazineSize);
                // jammedMult scales BOTH probabilities (AS3 reads pers.jammedMult into _loc5_ once
                // and multiplies each threshold by it). It is a drawback multiplier: a perk that
                // grants it makes the weapon jam more, not less.
                float jamMult = JammedMult;

                // Jam: weapon gets stuck, must reload to clear.
                if (rnd < breaking / holderSafe * jamMult)
                {
                    State.TRet = 2;
                    State.Jammed = true;
                    InitReload();
                    if (_debugSettings?.LogWeaponControllerDiagnostics == true)
                        Debug.Log($"[RangedWeaponController] Shoot() jammed weapon='{_def.weaponId}' breaking={breaking:0.###} rnd={rnd:0.###} jammedMult={jamMult:0.###}.");
                    return;
                }

                // Misfire: click, no damage.
                if (rnd < breaking / 5f * jamMult)
                {
                    State.TRet = 2;
                    if (_debugSettings?.LogWeaponControllerDiagnostics == true)
                        Debug.Log($"[RangedWeaponController] Shoot() misfire weapon='{_def.weaponId}' breaking={breaking:0.###} rnd={rnd:0.###} jammedMult={jamMult:0.###}.");
                    return;
                }
            }

            // ── Ammo check ────────────────────────────────────────────────────
            if (_def.magazineSize > 0 && State.CurrentAmmo < _def.ammoPerShot)
            {
                if (_debugSettings?.LogWeaponControllerDiagnostics == true)
                    Debug.Log($"[RangedWeaponController] Shoot() aborted by ammo weapon='{_def.weaponId}' currentAmmo={State.CurrentAmmo}.");
                return;
            }

            // ── Get muzzle position ────────────────────────────────────────────
            // AS3 Weapon.shoot() calls getBulXY() (:1461) before building the bullets, and that
            // reads `vis.emit` through vis.localToGlobal — so the round leaves the barrel tip and
            // follows the vis's flip and rotation. The presenter publishes exactly that point each
            // frame; falling back to the weapon origin reproduces AS3's own `else { bulX = X; }`.
            //
            // This used to be the weapon origin unconditionally, with a comment claiming the
            // spawner would apply WeaponVisualDefinition.muzzleLocalOffset. No spawner ever read
            // that field — it was 0 in all 129 weapon visuals and had no reader anywhere — so every
            // shot left the gun's registration point, i.e. its rear. Facing right the rear sits
            // behind the character and the error is invisible; facing left it is on screen, which
            // is the reported "turned left and it fires from the back".
            Vector2 muzzleWorld = MuzzleWorldPoint ?? new Vector2(State.X, State.Y);

            // ── Resolve the round this shot fires ──────────────────────────────
            // AS3 resolves the ammo node inside setAmmo and copies nine attributes off it onto the
            // weapon, where the shot then reads them. The port resolves through IAmmoResolver at fire
            // time instead of caching on the definition, for the same reason the ammo-type override
            // lives on the state: a cached copy on the shared ScriptableObject would leak a debug swap
            // to every wielder and past play-mode exit. One lookup per shot is a dictionary hit, not
            // an allocation — the registry path builds no strings.
            //
            // ResolvedAmmoType (not _def.ammoType) so the debug ammo swap actually changes what is
            // fired: reading the definition's own field here would leave a swapped weapon shooting its
            // original ballistics — the half-applied-swap shape the state's own comment warns about.
            AmmoDefinition ammo = _ammoResolver?.Resolve(State.ResolvedAmmoType);

            // ── Shared damage context (same for all pellets in this shot) ─────
            // The two attacker-side hit procs ride on the shot exactly as AS3 stamps them on the
            // bullet at fire time: critInvis straight off the owner (Weapon.as:1697), desintegr
            // through the weapon's cached Pers copy (:1525-1527). `_statSource` is the port's Pers
            // bridge; a null source answers 0 for both, which disables the procs and consumes no
            // random number — AS3's state for a unit with no Pers.
            DamageContext damCtx = DamageContext.FromWeapon(
                _def, null, State.OwnerFaction,
                // ── The weapon-skill channel (AS3 `_loc1_`, Weapon.as:1451-1459) ──────────────
                //
                // This is the base `Weapon.shoot()` path, so both halves apply here:
                //   :1454  `_loc1_ = this.weaponSkill` for the player (owner.weaponSkill for an NPC)
                //   :1516  `b.damage  = resultDamage(damage, _loc1_) * ammoDamage`
                //   :1531  `b.precision = resultPrec(owner.precMult, _loc1_)`
                //   :1523  `b.miss    = 1 - skillConf`  (skillConf set by checkAvail, :1366-1388)
                //
                // Read LIVE off the owner's skill levels rather than from a snapshot taken at equip
                // time, so spending a point moves the very next shot — AS3 re-derives both in
                // setParameters and re-copies them in Weapon.setPers on every recalc.
                //
                // A null `_statSource` is an enemy with no Pers: the unknown sentinel leaves the miss
                // chance at 0 and the multiplier at 1, which is exactly AS3's state for a unit whose
                // Pers was never set up (and the state these fixtures assert).
                //
                // `_def.skillLevel` is the numeric skill CODE (`<weapon skill='2'>`), not the required
                // level — that is `_def.weaponLevel`, which FromWeapon already uses for the miss term.
                // The same accessor backs CheckAvail(), so the gate and the penalty cannot drift apart.
                ownerWeaponSkillLevel: OwnerWeaponSkillLevel,
                weaponSkillMultiplier: WeaponSkillMultiplier,
                critInvisChance: _statSource != null ? _statSource.CritInvis : 0f,
                desintegrChance: _statSource != null ? _statSource.Desintegr : 0f,
                // AS3 stamps the bullet with resultPrec(owner.precMult, …) at fire time
                // (Weapon.as:1531, :1634), so the round's precision already carries the owner's
                // situational multiplier. 1 leaves the weapon's own value untouched — the state for a
                // shooter with no Pers, or an enemy.
                precisionMultiplier: _statSource != null ? _statSource.PrecisionMultiplier : 1f,
                // The round itself. FromWeapon folds its six fire-time terms (damage, pier, armor,
                // knock, prec, probiv) and its damage-type override into the context; the row also
                // rides along on DamageContext.Ammo so a later consumer (the burning effect, when a
                // status system exists) can still reach it without re-resolving the id.
                ammo: ammo);

            // ── Cues (same for all pellets) ───────────────────────────────────
            bool playSound = State.KolShoot % Mathf.Max(1, _def.magazineSize > 0 ? 1 : 1) == 0;
            // sndShoot_n is not currently a field on WeaponDefinition — default to 1.
            ShotCues cues = new ShotCues(
                playShootSound:   !string.IsNullOrEmpty(_def.soundShoot),
                spawnShellCasing: _def.hasShell,
                spawnMuzzleFlash: !string.IsNullOrEmpty(_def.muzzleFlareId),
                makeNoise:        _def.noiseRadius > 0f,
                noiseRadius:      _def.noiseRadius / PpuScale,
                shineRadius:      _def.shineRadius);

            // ── Pellet loop (kol in AS3) ───────────────────────────────────────
            // The deviation is drawn ONCE for the whole shot, before the loop opens — AS3 computes
            // `_loc2_` at :1460 and the loop only starts at :1463. Every pellet then shares the same
            // random offset; only the symmetric spread term below differs per pellet.
            float baseDevRad = CalculateDeviation();

            int pellets = Mathf.Max(1, _def.projectilesPerShot);
            for (int i = 0; i < pellets; i++)
            {
                // AS3 :1496's symmetric fan — the RAW `deviation` (not the breaking-scaled one the
                // shared term above uses). See WeaponSpreadMath.PelletSpread.
                float spreadOffset = WeaponSpreadMath.PelletSpread(i, pellets, _def.deviation);

                float pelletAngle = State.Rot
                    - State.RotUp * Mathf.Sign(State.X - _lastAimTarget.x) / 50f
                    + baseDevRad
                    + spreadOffset;

                _plans.Add(new ShotPlan(
                    origin:        ShotOrigin.MuzzlePoint,
                    worldPosition: muzzleWorld,
                    angleRad:      pelletAngle,
                    kind:          ShotKind.Projectile,
                    damage:        damCtx,
                    pelletIndex:   i,
                    totalPellets:  pellets,
                    cues:          i == 0 ? cues : ShotCues.None  // sound/shell only on first pellet
                ));

                if (_debugSettings?.LogWeaponControllerDiagnostics == true)
                {
                    Debug.Log(
                        $"[RangedWeaponController] Added ShotPlan weapon='{_def.weaponId}' pellet={i + 1}/{pellets} " +
                        $"pos={muzzleWorld} angle={pelletAngle:0.###} speedPxPerFrame={_def.projectileSpeed:0.###}.");
                }
            }

            // ── Post-shot state updates ────────────────────────────────────────
            // Consume ammo — unless the owner recycles the round (AS3 Weapon.as:1584-1595).
            if (_def.magazineSize > 0 && !RoundIsRecycled())
                State.CurrentAmmo = Mathf.Max(0, State.CurrentAmmo - _def.ammoPerShot);

            State.IsReloadingRP.Value = false; // not reloading if just fired

            // Consume durability (AS3: hp -= 1 + ammoHP, skipped in training/alicorn mode).
            // `ammoHP` is the `det` attribute — the round's extra wear, imported into
            // AmmoDefinition.extraDurabilityCost (Weapon.as:1598 `this.hp -= 1 + this.ammoHP`). It is
            // a bool in the port because no row carries a value other than 1, so a round either costs
            // one extra point or none. An unresolved round (null) adds nothing, matching AS3's
            // ammoHP = 0 default on the "неправильный патрон" branch.
            int durabilityCost = 1 + (ammo != null && ammo.extraDurabilityCost ? 1 : 0);
            State.CurrentDurability = Mathf.Max(0, State.CurrentDurability - durabilityCost);

            // Recoil (AS3 Weapon.as:1612-1617): the frame count is scaled, and a weapon that
            // recoils for more than 3 frames never drops below 3.
            State.TRet = Mathf.RoundToInt(_def.recoilFrames * RecoilMult);
            if (_def.recoilFrames > 3 && State.TRet < 3)
                State.TRet = 3;
            State.RotUp += _def.recoilLift * RecoilMult;

            // Animation trigger.
            if (_def.weaponVisual != null && _def.weaponVisual.shootFrameStart >= 0 && State.TShoot <= 1)
                State.TShoot = 3;

            State.KolShoot++;
            State.TAuto    = 3;
            State.IsShoot  = true;

            if (_debugSettings?.LogWeaponControllerDiagnostics == true)
            {
                Debug.Log(
                    $"[RangedWeaponController] Shoot() completed weapon='{_def.weaponId}' ammoNow={State.CurrentAmmo} " +
                    $"plansBuffered={_plans.Count} tAuto={State.TAuto}.");
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Rotation smoothing. Mirrors the drot block in Weapon.actions().
        /// Increments State.Rot toward rot2 by at most drot radians per frame.
        /// Sets State.Ready = true when aligned.
        /// </summary>
        private void AdvanceRotation(float rot2)
        {
            float drot = _def.bulletGravity > 0 ? 0f : 0f; // placeholder — drot is not a WeaponDefinition field yet
            // TODO: add drot field to WeaponDefinition (phis.@drot in AS3).
            // For now snap immediately (drot==0 path in AS3 → ready = true).
            State.Rot   = rot2;
            State.Ready = true;
        }

        /// <summary>
        /// The shot's muzzle-angle deviation, in radians — AS3 <c>_loc2_</c> in <c>Weapon.shoot</c>
        /// (<c>Weapon.as:1460</c>). The formula itself is <see cref="WeaponSpreadMath.Deviation"/>; this
        /// method is the composition — which live value feeds which argument.
        ///
        /// <para><b>Called once per shot, not once per pellet.</b> AS3 computes <c>_loc2_</c> at
        /// <c>:1460</c>, before the <c>kol</c> loop opens at <c>:1463</c>, and the loop adds it to
        /// every pellet's angle unchanged (<c>:1496</c>). Only the symmetric spread term differs per
        /// pellet. This method used to be called inside the loop, which drew a fresh random per pellet
        /// <i>and</i> consumed N numbers from the shared combat RNG stream where the oracle consumes
        /// one — the stream position matters here for the same reason it does in
        /// <see cref="RoundIsRecycled"/>.</para>
        ///
        /// <para><b>The three skill terms are all read live.</b> <c>_loc1_</c> comes off the same
        /// accessor the damage path uses, <c>skillConf</c> off the same <c>checkAvail</c> rule the fire
        /// gate uses, and <c>mazil</c> off the stat source. AS3 snapshots all three in <c>setPers</c>;
        /// reading them live means a point spent mid-fight tightens the very next shot instead of
        /// waiting for a re-equip.</para>
        /// </summary>
        private float CalculateDeviation()
        {
            return WeaponSpreadMath.Deviation(
                random01:              _rng.NextFloat(),
                deviation:             _def.deviation,
                breaking:              State.Breaking(),
                skillConfidence:       HitAvoidance.SkillConfidence(_def.weaponLevel, OwnerWeaponSkillLevel),
                weaponSkillMultiplier: WeaponSkillMultiplier,
                mazil:                 _statSource != null ? _statSource.MazilAdd : 0f);
        }

        /// <summary>
        /// Whether this shot leaves the magazine alone because the owner recycled the round.
        /// Mirrors AS3 <c>Weapon.as:1586</c>:
        /// <code>
        /// if(!(this.owner.player &amp;&amp; pers.recyc > 0
        ///      &amp;&amp; (this.ammo == "batt" || this.ammo == "energ" || this.ammo == "crystal")
        ///      &amp;&amp; Math.random() &lt; pers.recyc))
        ///    this.hold -= this.rashod;
        /// </code>
        ///
        /// <para><b>The short-circuit order is load-bearing.</b> AS3 only rolls <c>Math.random()</c>
        /// once the first three conditions hold, and the combat RNG is a shared stream — drawing a
        /// number here for a weapon that could never recycle would shift every later roll. So the
        /// guard clauses must stay ahead of <c>NextFloat()</c>.</para>
        ///
        /// <para>Recycling is limited to energy ammo on purpose: a recycling ballistic weapon would
        /// refill its magazine from nothing.</para>
        /// </summary>
        private bool RoundIsRecycled()
        {
            if (_statSource == null) return false;
            float chance = _statSource.Recyc;
            if (chance <= 0f) return false;
            // ResolvedAmmoType, not _def.ammoType: the debug ammo swap sets a per-instance override, and a
            // read that skipped it would let a weapon recycle on its original energy type after being
            // switched to ballistic.
            string ammoType = State.ResolvedAmmoType;
            if (ammoType != "batt" && ammoType != "energ" && ammoType != "crystal")
                return false;
            return _rng.NextFloat() < chance;
        }

        /// <summary>
        /// Start reload sequence. Mirrors Weapon.initReload().
        /// </summary>
        private void InitReload()
        {
            if (State.TReload > 0) return; // already reloading
            if (_def.magazineSize <= 0)    return; // no magazine (melee, unarmed, "not" ammo)
            if (State.ResolvedAmmoType == "not") return; // infinite-ammo weapon — never reloads

            // NOTE: State.Jammed is deliberately NOT cleared here. AS3 clears it in
            // reloadWeapon() (Weapon.as:1710) — the function that FILLS the magazine, which this
            // class models as CompleteReload(). Clearing it at reload START made the flag dead:
            // Shoot() sets Jammed = true and then calls InitReload() in the same frame, so no
            // reader could ever observe a jam. CompleteReload() still clears it.

            if (_def.reloadTime > 0)
            {
                _reloadTotal                 = Mathf.RoundToInt(_def.reloadTime * ReloadMult);
                State.TReload                = _reloadTotal;
                State.IsReloadingRP.Value    = true;
                State.ReloadProgressRP.Value = 0f;
                // Presenter will play reload animation from TReload > 0 check.
            }
            else
            {
                // Instant reload (no reload time).
                CompleteReload();
            }
        }

        /// <summary>
        /// Fill magazine from inventory. Mirrors Weapon.reloadWeapon() in AS3.
        ///
        /// Three cases:
        ///   "recharg" ammo  — self-regenerating; just fill, no inventory deduction.
        ///   _ammoSource null — training / no-inventory mode; fill unconditionally.
        ///   otherwise       — pull (magazineSize - CurrentAmmo) rounds from IAmmoSource.
        ///                     If the source has fewer than one shot's worth, the reload
        ///                     completes with however many rounds are available (possibly 0).
        /// </summary>
        private void CompleteReload()
        {
            if (_def.rechargeFrames > 0) return; // recharge weapons self-tick via TRech

            int toLoad;

            // Read once, use for the branch, the inventory calls and the log. A second read of the
            // definition's own type here would make a debug ammo swap half-applied — the reload would
            // fill from the wrong inventory bucket while the guard above used the override.
            string ammoType = State.ResolvedAmmoType;

            if (ammoType == "recharg" || _ammoSource == null)
            {
                // Training mode or self-recharging weapon — fill unconditionally.
                toLoad = _def.magazineSize - State.CurrentAmmo;
            }
            else
            {
                int needed    = _def.magazineSize - State.CurrentAmmo;
                int available = _ammoSource.GetAmmoCount(ammoType);
                toLoad        = Mathf.Min(needed, available);
                if (toLoad > 0)
                    _ammoSource.ConsumeAmmo(ammoType, toLoad);

                if (_debugSettings?.LogWeaponControllerDiagnostics == true)
                    Debug.Log($"[RangedWeaponController] CompleteReload weapon='{_def.weaponId}' " +
                              $"ammoType='{ammoType}' needed={needed} available={available} loaded={toLoad}.");
            }

            State.CurrentAmmo           += toLoad;
            State.TReload                = 0;
            State.Jammed                 = false;
            State.IsReloadingRP.Value    = false;
            State.ReloadProgressRP.Value = 1f;
        }
    }
}
