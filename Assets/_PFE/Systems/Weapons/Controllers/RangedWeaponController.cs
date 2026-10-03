using System.Collections.Generic;
using UnityEngine;
using PFE.Core;
using PFE.Data.Definitions;
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

        // AS3 copies these four onto the weapon in setPers (Weapon.as:973-977) and the weapon then
        // reads its own copy. The port reads the live source instead, so a perk taken mid-fight
        // applies to the next shot without re-equipping.
        private float ReloadMult => _statSource != null ? _statSource.ReloadMult : 1f;
        private float RecoilMult => _statSource != null ? _statSource.RecoilMult : 1f;
        private float JammedMult => _statSource != null ? _statSource.JammedMult : 1f;

        // Total frames at reload start — used to compute ReloadProgressRP each tick.
        private int _reloadTotal;

        // Shot plan accumulator — filled during flash frames, flushed by FlushShotPlans.
        private readonly List<ShotPlan> _plans = new();
        private readonly PFE.Core.Rng.IRngService _rng;

        // ── Constructor ───────────────────────────────────────────────────────

        public RangedWeaponController(WeaponRuntimeState state, PfeDebugSettings debugSettings = null,
                                      IAmmoSource ammoSource = null, PFE.Core.Rng.IRngService rng = null,
                                      IWeaponStatSource statSource = null)
        {
            State          = state;
            _def           = state.Def;
            _debugSettings = debugSettings;
            _ammoSource    = ammoSource;
            _statSource    = statSource;
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
            // Muzzle offset applied in world space from current weapon position.
            // The WeaponPresenter has the actual Transform; we use State.X/Y as origin
            // and WeaponVisualDefinition.muzzleLocalOffset will be applied by spawner.
            Vector2 muzzleWorld = new Vector2(State.X, State.Y);

            // ── Shared damage context (same for all pellets in this shot) ─────
            DamageContext damCtx = DamageContext.FromWeapon(_def, null, State.OwnerFaction);

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
            int pellets = Mathf.Max(1, _def.projectilesPerShot);
            for (int i = 0; i < pellets; i++)
            {
                // Per-pellet deviation (AS3: (rnd-0.5)*deviation*...*PI/180 + pellet spread).
                float baseDevRad = CalculateDeviation();
                // Spread offset for multi-pellet: each pellet offset by (i - (kol-1)/2) * dev/2
                float spreadOffset = pellets > 1
                    ? (i - (pellets - 1) / 2f) * _def.deviation * Mathf.PI / 360f
                    : 0f;

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
            State.CurrentDurability = Mathf.Max(0, State.CurrentDurability - 1);

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
        /// Deviation angle in radians for one pellet.
        /// AS3: (rnd-0.5) * deviation / skillConf / (skill+0.01) * PI/180 * devMult
        /// Simplified: use definition deviation scaled by breaking.
        /// </summary>
        private float CalculateDeviation()
        {
            float breaking = State.Breaking();
            float effective = _def.deviation * (1f + breaking * 2f);
            return (_rng.NextFloat() - 0.5f) * effective * Mathf.Deg2Rad;
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
            if (_def.ammoType != "batt" && _def.ammoType != "energ" && _def.ammoType != "crystal")
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
            if (_def.ammoType == "not")    return; // infinite-ammo weapon — never reloads

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

            if (_def.ammoType == "recharg" || _ammoSource == null)
            {
                // Training mode or self-recharging weapon — fill unconditionally.
                toLoad = _def.magazineSize - State.CurrentAmmo;
            }
            else
            {
                int needed    = _def.magazineSize - State.CurrentAmmo;
                int available = _ammoSource.GetAmmoCount(_def.ammoType);
                toLoad        = Mathf.Min(needed, available);
                if (toLoad > 0)
                    _ammoSource.ConsumeAmmo(_def.ammoType, toLoad);

                if (_debugSettings?.LogWeaponControllerDiagnostics == true)
                    Debug.Log($"[RangedWeaponController] CompleteReload weapon='{_def.weaponId}' " +
                              $"ammoType='{_def.ammoType}' needed={needed} available={available} loaded={toLoad}.");
            }

            State.CurrentAmmo           += toLoad;
            State.TReload                = 0;
            State.Jammed                 = false;
            State.IsReloadingRP.Value    = false;
            State.ReloadProgressRP.Value = 1f;
        }
    }
}
