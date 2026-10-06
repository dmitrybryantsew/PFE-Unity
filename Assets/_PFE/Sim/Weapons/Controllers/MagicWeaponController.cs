using System.Collections.Generic;
using UnityEngine;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Systems.Combat;

namespace PFE.Systems.Weapons.Controllers
{
    /// <summary>
    /// Full AS3-parity implementation of WMagic.as for tip==5 magic weapons.
    ///
    /// Key differences from RangedWeaponController:
    ///   - Position SNAPS to hornPoint every tick (no lerp) — AS3: X = owner.magicX; Y = owner.magicY
    ///   - Rotation still calculated (atan2 to aim target) but WeaponPresenter applies it normally
    ///   - Dual resource cost, read live from <see cref="IManaSource"/>:
    ///       magicPoolCost  → owner.mana   (the regenerating budget, `ammo@magic`)
    ///       manaHealthCost → pers.manaHP  (the mana organ, `ammo@mana`)
    ///   - Resource failure → t_rel = t_prep * 3 (lockout timer)
    ///   - Mana is consumed AFTER the shot is emitted (mirrors WMagic.shoot() calling super first)
    ///   - NO durability charge — AS3's Weapon.shoot() charges `hp` only for
    ///     `owner.player && tip < 4 && tip != 0` (Weapon.as:1596), which excludes tip 5
    ///   - No magazine/reload — magic weapons are resource-gated, not ammo-gated
    ///
    /// AS3 source: WMagic.as + Weapon.as (tip==5 branches)
    ///
    /// <para><b>NOT ported — recorded rather than silently missing:</b></para>
    /// <list type="bullet">
    /// <item><b>The <c>respect == 1</c> half of the attack gate</b> (<c>WMagic.as:40-45</c>, the
    /// <c>"disSpell"</c> refusal). The <c>spellsPoss</c> half <b>is</b> modelled — see
    /// <see cref="CanCastSpells"/>. <c>respect</c> is <i>weapon-instance</i> state, not owner state:
    /// AS3 keeps it on <c>Weapon.respect</c> (<c>Weapon.as:108</c>), toggled by
    /// <c>Invent.respectWeapon()</c> (<c>Invent.as:830-855</c>), and the port keeps it on
    /// <c>GameWeaponInstance.Respect</c>, which no controller holds. The gate is also currently
    /// <b>unreachable</b>: <c>GameInventory.ToggleWeaponRespect</c> unequips a weapon the moment it
    /// goes Hidden (<c>GameInventory.cs:397-400</c>), so an equipped weapon can never be
    /// <c>respect == 1</c>. Modelling it would add a seam nothing can set; it waits on whichever
    /// change makes hiding an equipped weapon possible.</item>
    /// <item>The <c>infoText</c> / <c>gui.bulb</c> / <c>Snd.ps</c> feedback for every refusal
    /// (<c>WMagic.as:35-37</c>, <c>:42-43</c>, <c>:63-65</c>, <c>:83-85</c>) — this path has no
    /// message layer.</item>
    /// <item><c>setPers</c> <b>damage</b> scaling (<c>WMagic.as:92-108</c>): <c>damMult</c> ×
    /// <c>pers.spellsDamMult</c>, plus the <c>resultDamage</c>/<c>resultPrec</c> overrides
    /// (<c>resultDamage</c> drops the <c>skillPlusDam</c> and <c>breaking</c> terms the base class
    /// applies). The mana half of <c>setPers</c> <b>is</b> modelled, via
    /// <see cref="IManaSource.ManaCostMultiplier"/>.</item>
    /// </list>
    ///
    /// <para>The <c>spell='1'</c> family (<c>sp_slow</c>, <c>sp_mwall</c>, …) never reaches this
    /// class. It is not a projectile weapon at all in AS3 — it is cast through <c>Spell.as</c> via
    /// <c>Invent.useItem</c> — and <c>WeaponControllerFactory</c> refuses to build a controller for it
    /// (<c>WeaponControllerFactory.cs:109-116</c>). See the audit's §3.</para>
    /// </summary>
    public sealed class MagicWeaponController : IWeaponController
    {
        // ── Constants ─────────────────────────────────────────────────────────

        private const float PpuScale  = 100f;

        // ── State ─────────────────────────────────────────────────────────────

        public WeaponRuntimeState State { get; }
        private readonly IWeaponStats _def;

        /// <summary>
        /// Live mana, or null for "no tracking" (see <see cref="IManaSource"/>). A null source skips
        /// both the gate and the spend, which is the port's behaviour for a rig with no
        /// <c>CharacterStats</c> — and AS3's state for a unit whose mana was never initialised.
        /// </summary>
        private readonly IManaSource _mana;

        /// <summary>
        /// The owner's <c>Pers</c>-derived multipliers, used here for the <c>checkAvail</c> skill gate.
        /// Null means "no owner stats" — <see cref="HitAvoidance.UnknownOwnerSkillLevel"/> then makes
        /// the gate fail open, which is AS3's state for a unit whose <c>Pers</c> was never set up.
        /// </summary>
        private readonly IWeaponStatSource _statSource;

        private float _frameAccum;
        private bool  _attackHeld;

        private readonly List<ShotPlan> _plans = new();

        // ── Constructor ───────────────────────────────────────────────────────

        public MagicWeaponController(WeaponRuntimeState state, IManaSource manaSource = null,
                                     IWeaponStatSource statSource = null)
        {
            State      = state;
            _def       = state.Def;
            _mana      = manaSource;
            _statSource = statSource;
        }

        // ── Owner stats ───────────────────────────────────────────────────────

        /// <summary>
        /// The owner's tier in this weapon's skill — AS3 <c>pers.getWeapLevel(skill)</c>. The sentinel
        /// means "no stat source", which makes <see cref="HitAvoidance.CanFire"/> fail open.
        /// </summary>
        private int OwnerWeaponSkillLevel => _statSource != null
            ? _statSource.OwnerWeaponSkillLevel(_def.skillLevel)
            : HitAvoidance.UnknownOwnerSkillLevel;

        /// <summary>
        /// AS3 <c>Weapon.checkAvail()</c> (<c>Weapon.as:1366-1388</c>) — may this owner fire? Only the
        /// refuse half is here (gap &gt; 2); the 0.8/0.6 penalties are the round's miss term and live
        /// in <c>HitAvoidance.MissChance</c>, as they do for ranged.
        ///
        /// <para><b>This was missing from magic only.</b> <c>WMagic.attack()</c> calls it at
        /// <c>WMagic.as:46-52</c>, inside the same <c>owner.player</c> guard as the <c>spellsPoss</c>
        /// and <c>respect</c> checks. Ranged, thrown and melee all gate; magic did not, so a
        /// level-1 character could cast <c>fireball</c> (weaponLevel 12, magic skill 6) that AS3
        /// refuses outright.</para>
        ///
        /// <para>A refusal arms <b>no</b> lockout — AS3 returns <c>false</c> and the caller clears the
        /// trigger latch; there is no <c>t_rel</c> on this branch.</para>
        /// </summary>
        private bool CheckAvail() => HitAvoidance.CanFire(_def.weaponLevel, OwnerWeaponSkillLevel);

        /// <summary>
        /// AS3 <c>owner.player &amp;&amp; World.w.pers.spellsPoss == 0</c> (<c>WMagic.as:33</c>) — the
        /// magic-only gate that runs ahead of <see cref="CheckAvail"/>. The predicate itself is
        /// <see cref="HitAvoidance.CanCastSpells"/>.
        ///
        /// <para><b>A null mana source fails open, and that is the oracle's behaviour.</b> AS3 guards
        /// the whole gate on <c>owner.player</c>; the port's encoding of "not a player" is "no owner
        /// state", the same convention <see cref="CheckAvail"/> and <see cref="ManaCostMult"/> already
        /// use. So an enemy — which has no <c>CharacterStats</c> — is never refused by this gate,
        /// exactly as in AS3.</para>
        /// </summary>
        private bool CanCastSpells() =>
            _mana == null || HitAvoidance.CanCastSpells(_mana.SpellsPossible);

        /// <summary>
        /// The owner's mana-cost multiplier — <c>pers.allDManaMult * pers.warlockDManaMult</c>
        /// (<c>WMagic.setPers</c>, <c>WMagic.as:92-98</c>). 1 with no mana source, which is
        /// irrelevant there because a null source also skips the gate and the spend entirely.
        /// </summary>
        private float ManaCostMult => _mana?.ManaCostMultiplier ?? 1f;

        /// <summary>The budget debit this shot will make — <c>dmagic</c>, i.e. <c>magic * mult</c>.</summary>
        private float PoolCost => _def.magicPoolCost * ManaCostMult;

        /// <summary>The organ wound this shot will inflict — <c>dmana</c>, i.e. <c>mana * mult</c>.</summary>
        private float OrganCost => _def.manaHealthCost * ManaCostMult;

        /// <summary>
        /// AS3 <c>Weapon.animated</c> (<c>Weapon.as:84</c>) — "this visual has an animation to play,
        /// so arm the shoot lockout". <c>Weapon.getXmlParam</c> raises it when the vis has more than
        /// one frame (<c>Weapon.as:504-507</c>); <c>WMagic</c>'s constructor then <b>clears</b> it
        /// whenever <c>prep</c> is set (<c>WMagic.as:15-18</c>), because a charge-up weapon's frames
        /// are driven by <c>t_prep</c> through <c>gotoAndStop</c> instead
        /// (<c>Weapon.as:1975-1991</c>) — a <c>gotoAndPlay("shoot")</c> would fight that.
        ///
        /// <para><b><c>mray</c> is the only weapon in AllData.as where both conditions hold</b>: it is
        /// tip 5 with <c>prep='18'</c>. Before this predicate existed the port armed <c>TShoot</c>
        /// for it, and the presenter's shoot branch (which returns before its prep branch) shadowed
        /// the charge frames — so the charge-up animation never appeared. AS3 shows the charge frames
        /// and nothing else.</para>
        ///
        /// <para>The reload half of <c>animated</c> (<c>Weapon.as:1940</c>) has no counterpart here:
        /// magic weapons never reload.</para>
        /// </summary>
        private bool Animated => _def.prepFrames <= 0
                                 && _def.weaponVisual != null
                                 && _def.weaponVisual.shootFrameStart >= 0;

        // ── IWeaponController ─────────────────────────────────────────────────

        public void BeginAttack() => _attackHeld = true;
        public void EndAttack()   => _attackHeld = false;
        public void StartReload() { }  // Magic weapons don't reload

        public void Tick(float dt, Vector2 holdPoint, Vector2 hornPoint, Vector2 aimTarget)
        {
            _frameAccum += dt * SimClock.FramesPerSecond;
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

            State.IsShoot   = false;
            State.WasAttack = State.IsAttack;
            State.IsAttack  = false;
        }

        /// <summary>
        /// Mirrors WMagic.attack().
        /// Checks spell availability and mana, sets TAttack if OK, TRel if not.
        /// </summary>
        private void RunAttack()
        {
            // ── t_auto tap debounce (WMagic.as:23-27) ─────────────────────────
            //   if(!param1 && !World.w.alicorn && !auto && t_auto > 0) { t_auto = 3; return false; }
            // The port has no `param1` force flag (that is the AI/auto-attack path) and no alicorn
            // state, so both are false. Same gate shape as the ranged / thrown / unarmed controllers;
            // it was simply absent here, so a non-auto magic weapon could fire every frame the
            // trigger was held instead of once per tap.
            if (!_def.IsAuto && State.TAuto > 0)
            {
                State.TAuto = 3;
                return;
            }

            // ── Spell-permission gate (WMagic.as:33-39) ───────────────────────
            // `if(owner.player && World.w.pers.spellsPoss == 0) { gui.infoText("noSpells");
            //  gui.bulb(X,Y); Snd.ps("nomagic"); return false; }`
            // The first gate in attack(), and a hard stop: it returns before checkAvail() and before
            // either `t_rel` assignment, so a caster with no spells is refused WITHOUT a lockout —
            // holding the trigger does nothing, frame after frame. See HitAvoidance.CanCastSpells.
            if (!CanCastSpells())
            {
                if (Debug.isDebugBuild)
                    Debug.Log($"[MagicWeaponController] Refused: no spell casting available " +
                              $"(spellsPoss == 0) for '{_def.weaponId}'.");
                return;
            }

            // ── Skill gate (WMagic.as:46-52) ──────────────────────────────────
            // `if(owner.player) { if(!checkAvail()) return false; }` — runs BEFORE is_attack and the
            // prep charge, so a refused cast leaves no trace beyond the refusal itself.
            if (!CheckAvail())
            {
                if (Debug.isDebugBuild)
                    Debug.Log($"[MagicWeaponController] Refused: owner skill tier {OwnerWeaponSkillLevel} " +
                              $"is more than 2 below weaponLevel {_def.weaponLevel} for '{_def.weaponId}'.");
                return;
            }

            State.IsAttack = true;

            // Prep charge (same as ranged).
            if (State.TPrep < _def.prepFrames + 10)
                State.TPrep += 2;

            // Only arm t_attack once prep is satisfied and no attack pending.
            if (State.TPrep < _def.prepFrames) return;
            if (State.TAttack > 0)             return;

            // ── Mana gate (WMagic.as:58-88) ───────────────────────────────────
            if (_mana != null)
            {
                // Organ first: `if(owner.player && dmana > pers.manaHP) t_rel = t_prep*3`.
                // `OrganCost` is `dmana` — the raw `mana` attribute through the Pers multiplier, the
                // same value the spend will use, so the gate and the debit cannot disagree.
                if (OrganCost > 0f && OrganCost > _mana.ManaHp)
                {
                    State.TRel = State.TPrep * 3;
                    if (Debug.isDebugBuild)
                        Debug.Log("[MagicWeaponController] No mana HP — locked out.");
                    return;
                }

                // Then the budget: `else if(dmagic <= owner.mana || owner.mana >= owner.maxmana*0.99)`.
                bool hasEnoughMana = PoolCost <= _mana.MagicMana ||
                                     _mana.MagicMana >= _mana.MaxMagicMana * 0.99f;
                if (!hasEnoughMana)
                {
                    State.TRel = State.TPrep * 3;
                    if (Debug.isDebugBuild)
                        Debug.Log("[MagicWeaponController] Not enough mana — locked out.");
                    return;
                }
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
            if (State.IsBroken) return;

            // Build damage context and cues.
            DamageContext damCtx = DamageContext.FromWeapon(_def, null, State.OwnerFaction);

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
                    cues:          i == 0 ? cues : ShotCues.None
                ));
            }

            // ── Consume mana AFTER emitting (mirrors WMagic.shoot() calling super first) ──
            // AS3: owner.mana -= dmagic; owner.dmana = 0; World.w.pers.manaDamage(dmana);
            // Same `PoolCost`/`OrganCost` the gate above tested, so the two halves of the cost can
            // never drift apart — the shape of bug that let magic be free in the first place.
            _mana?.SpendMana(PoolCost, OrganCost);

            // ── Post-shot state ───────────────────────────────────────────────
            // No durability charge. AS3's Weapon.shoot() (Weapon.as:1596) reads
            //   if(owner.player && tip < 4 && tip != 0 && !(loc.train || World.w.alicorn)) hp -= 1 + ammoHP;
            // — tip 5 is excluded, so magic weapons never wear out. The port charged 1 per shot.
            State.TRet   = _def.recoilFrames;
            State.RotUp += _def.recoilLift;
            // `if(this.animated && this.t_shoot <= 1) { vis.gotoAndPlay("shoot"); t_shoot = 3; }`
            // (Weapon.as:1600). `Animated` is false for a charge-up weapon, so mray never arms the
            // lockout and the presenter falls through to its prep branch.
            if (Animated && State.TShoot <= 1)
                State.TShoot = 3;
            State.KolShoot++;
            State.TAuto   = 3;
            State.IsShoot = true;
        }
    }
}
