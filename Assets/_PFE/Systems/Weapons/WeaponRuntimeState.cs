using R3;
using PFE.Data.Definitions;
using UnityEngine;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// All mutable state for a single equipped weapon instance.
    ///
    /// Plain C# class — no MonoBehaviour. Owned by PlayerWeaponLoadout, passed
    /// into the IWeaponController every Tick(). UI binds to the reactive properties.
    ///
    /// Field names mirror AS3 Weapon.as so diffs against the source stay readable.
    ///   hold      → CurrentAmmo
    ///   hp        → CurrentDurability
    ///   t_attack  → TAttack
    ///   t_reload  → TReload
    ///   t_prep    → TPrep
    ///   t_ret     → TRet
    ///   rotUp     → RotUp
    ///   t_rech    → TRech
    ///   t_rel     → TRel      (magic lockout)
    ///   t_shoot   → TShoot    (animation lockout after shoot)
    ///   kol_shoot → KolShoot  (shot counter for sndShoot_n)
    /// </summary>
    public sealed class WeaponRuntimeState
    {
        // ── Definition reference ───────────────────────────────────────────────
        public readonly WeaponDefinition Def;

        // ── Ammo type ─────────────────────────────────────────────────────────

        /// <summary>
        /// Per-instance ammo-type override, or null to use <see cref="WeaponDefinition.ammoType"/>.
        ///
        /// <para><b>Why this lives here and not on the definition.</b> <see cref="WeaponDefinition"/> is a
        /// shared <c>ScriptableObject</c>: every wielder of that weapon points at the one asset, and in the
        /// editor a write survives play-mode exit. Mutating it to "swap the ammo" would therefore change
        /// the weapon for every unit carrying it and persist past the session — the debug tool would be
        /// editing the game's data, not a running instance. This field is the per-instance seam.</para>
        ///
        /// <para><b>Read through <see cref="ResolvedAmmoType"/>, never directly.</b> Four sites in
        /// <c>RangedWeaponController</c> decide reload/recycle behaviour from the ammo type; if any of them
        /// kept reading <c>Def.ammoType</c> the dropdown would appear to work while half the behaviour
        /// ignored it — the "inert fix" shape this project keeps hitting.</para>
        /// </summary>
        public string AmmoTypeOverride;

        /// <summary>
        /// The ammo type actually in effect: the override when one is set, else the definition's own.
        /// Single source of truth for every reader.
        /// </summary>
        public string ResolvedAmmoType =>
            string.IsNullOrEmpty(AmmoTypeOverride) ? Def?.ammoType : AmmoTypeOverride;

        /// <summary>
        /// True when the type in effect differs from the definition's original — i.e. a debug swap is
        /// active. Used by the overlay to label the state honestly rather than showing a selection that
        /// looks like the weapon's own ammo.
        /// </summary>
        public bool HasAmmoTypeOverride =>
            !string.IsNullOrEmpty(AmmoTypeOverride) &&
            !string.Equals(AmmoTypeOverride, Def?.ammoType, System.StringComparison.Ordinal);

        /// <summary>
        /// Set or clear the override. Passing the definition's own type clears it, so "back to regular"
        /// leaves no override behind and <see cref="HasAmmoTypeOverride"/> stays truthful.
        /// </summary>
        public void SetAmmoTypeOverride(string ammoType)
        {
            AmmoTypeOverride = string.Equals(ammoType, Def?.ammoType, System.StringComparison.Ordinal)
                ? null
                : ammoType;
        }

        // ── Frame-counter timers (30 fps cadence, matching AS3) ────────────────
        public int   TAttack;    // countdown frames until next allowed fire
        public int   TReload;    // reload countdown
        public int   TPrep;      // charge-up progress (increments while held)
        public int   TRet;       // recoil push-back duration
        public int   TRech;      // self-recharge cadence timer
        public int   TRel;       // magic lockout after resource failure
        public int   TShoot;     // animation lockout (prevents retriggering shoot anim)
        public int   TAuto;      // "auto" cooldown (single-shot tap debounce)
        public int   Pow;        // power-charge accumulator (held frames while t_auto>0)
        public float RotUp;      // angular barrel lift (degrees); decays each frame — rotUp in AS3

        // ── Magazine / durability ──────────────────────────────────────────────
        public int CurrentAmmo;         // hold in AS3 — bullets in current magazine
        public int CurrentDurability;   // hp in AS3

        // ── Jam state ─────────────────────────────────────────────────────────
        public bool Jammed;

        // ── Per-shot counters ──────────────────────────────────────────────────
        public int KolShoot;     // increments each shot — used for sndShoot_n gating

        // ── Input edge-detection (set by controller from BeginAttack/EndAttack) ──
        public bool IsAttack;    // attack is held this tick
        public bool WasAttack;   // attack was held last tick (for prep-sound edge)
        public bool IsShoot;     // a bullet fired this tick (reset each tick start)

        // ── Aim state ─────────────────────────────────────────────────────────
        public bool  Ready;      // weapon has rotated to aim target (drot reached)
        public float Rot;        // current weapon angle in radians (world)

        // ── World-space position (lerped toward hold point, matches AS3 X/Y) ───
        public float X;
        public float Y;

        /// <summary>
        /// Faction of the unit that owns this weapon — the attacker side of
        /// <see cref="FactionRule"/>, and what AS3 reads as <c>this.weap.owner.fraction</c>.
        ///
        /// <para>It lives on the state rather than on the controller because the state is created by
        /// <c>WeaponControllerFactory</c> from a definition alone, with no unit in scope, while the
        /// loadout that equips the weapon <i>does</i> know whose it is. Set once at equip time;
        /// defaults to <see cref="FactionType.Neutral"/>, which is AS3's own default
        /// (<c>Unit.as:454</c>) and makes an unowned weapon hit everyone.</para>
        /// </summary>
        public FactionType OwnerFaction = FactionType.Neutral;

        // ── Reactive properties for UI (HUD ammo counter, reload bar) ─────────
        public readonly ReactiveProperty<int>   CurrentAmmoRP;
        public readonly ReactiveProperty<int>   CurrentDurabilityRP;
        public readonly ReactiveProperty<bool>  IsReloadingRP;
        public readonly ReactiveProperty<float> ReloadProgressRP;

        // ── Constructor ────────────────────────────────────────────────────────

        public WeaponRuntimeState(WeaponDefinition def)
        {
            Def              = def;
            CurrentAmmo      = def.magazineSize;
            CurrentDurability = def.maxDurability;
            TRech            = def.rechargeFrames;

            // Self-recharge weapons start full.
            if (def.rechargeFrames > 0)
                CurrentAmmo = def.magazineSize;

            CurrentAmmoRP        = new ReactiveProperty<int>(CurrentAmmo);
            CurrentDurabilityRP  = new ReactiveProperty<int>(CurrentDurability);
            IsReloadingRP        = new ReactiveProperty<bool>(false);
            ReloadProgressRP     = new ReactiveProperty<float>(0f);
        }

        // ── Helpers called by controllers ──────────────────────────────────────

        /// <summary>Push reactive props to match internal fields. Call after mutating ammo/durability.</summary>
        public void SyncReactive()
        {
            CurrentAmmoRP.Value       = CurrentAmmo;
            CurrentDurabilityRP.Value = CurrentDurability;
        }

        /// <summary>
        /// Durability as 0–1 fraction past the halfway point.
        /// Matches AS3: breaking = (maxhp - hp) / maxhp * 2 - 1 when hp < maxhp/2.
        /// </summary>
        public float Breaking()
        {
            if (CurrentDurability >= Def.maxDurability / 2) return 0f;
            return Mathf.Clamp01((Def.maxDurability - CurrentDurability) / (float)Def.maxDurability * 2f - 1f);
        }

        /// <summary>True when magazine has fewer rounds than one shot costs.</summary>
        public bool NeedsReload => Def.magazineSize > 0 && CurrentAmmo < Def.ammoPerShot;

        /// <summary>True when the weapon is fully broken.</summary>
        public bool IsBroken => CurrentDurability <= 0;

        public void Dispose()
        {
            CurrentAmmoRP.Dispose();
            CurrentDurabilityRP.Dispose();
            IsReloadingRP.Dispose();
            ReloadProgressRP.Dispose();
        }
    }
}
