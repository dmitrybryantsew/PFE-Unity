using R3;
using PFE.Data.Definitions;
using PFE.Systems.Combat;

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
        public readonly IWeaponStats Def;

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

        /// <summary>
        /// This weapon's trigger has no <b>click edge</b> — it is fired by a system that holds it, so
        /// the single-shot debounce must not apply. AS3 spells the same thing as <c>attack(true)</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>What the debounce is for, and the case it breaks.</b> <c>Weapon.attack()</c>
        /// (<c>Weapon.as:1297-1302</c>) is
        /// <c>if(!param1 &amp;&amp; !World.w.alicorn &amp;&amp; !this.auto &amp;&amp; this.t_auto &gt; 0) { this.t_auto = 3;
        /// ++this.pow; return true; }</c> — a semi-auto weapon fires once per <i>press</i>, because
        /// <c>t_auto</c> is only allowed to expire while nobody is calling <c>attack()</c>. The player
        /// supplies that gap by releasing the key; the <c>sats</c> turret sidesteps it entirely by
        /// calling <c>attack(true)</c> (<c>UnitPlayer.as:1161</c>), which skips the branch.</para>
        ///
        /// <para><b><c>UnitNPC.control()</c> does neither.</b> It calls <c>currentWeapon.attack()</c>
        /// with no argument on <b>every frame</b> while a live <c>celUnit</c> is in sight
        /// (<c>UnitNPC.as:570-577</c>), and <c>Weapon.step()</c> — the only place <c>t_auto</c> decays
        /// (<c>:1226</c>) — runs <i>after</i> <c>control()</c> in the same frame
        /// (<c>Unit.as:1806-1852</c>). So <c>t_auto</c> is refreshed to 3 and decayed to 2 forever, the
        /// branch is taken on every frame after the first shot, and the NPC fires <b>exactly once per
        /// engagement</b> and is locked out until its target leaves <c>sost == 1</c>.</para>
        ///
        /// <para>Raiders do not hit this because their call is intermittent — <c>isrnd(0.1)</c>
        /// (<c>UnitRaider.as:1521</c>) — which is the gap the debounce needs. An NPC's is
        /// unconditional, which is what turns a debounce into a permanent lockout.</para>
        ///
        /// <para><b>Recorded divergence, not a transcription.</b> Setting this does not reproduce the
        /// oracle: it makes a held NPC trigger fire at the weapon's authored <c>rapid</c> cadence, which
        /// is what <c>attack(true)</c> means for <c>sats</c>. Set by
        /// <c>UnitNPCController.EquipPersonaWeapons</c> only — every other owner keeps the debounce, so
        /// the player's semi-autos and the raiders' <c>isrnd</c> cadence are untouched. Deliberately
        /// NOT honoured by <c>MeleeWeaponController</c>, whose same branch also accumulates
        /// <c>Pow</c> for the power attack (<c>WClub.as:340-351</c>) — its lockout and its charge are
        /// one code path and need their own decision.</para>
        /// </remarks>
        public bool AutomaticTrigger;

        // ── Magazine / durability ──────────────────────────────────────────────
        public int CurrentAmmo;         // hold in AS3 — bullets in current magazine
        public int CurrentDurability;   // hp in AS3

        // ── Jam state ─────────────────────────────────────────────────────────
        public bool Jammed;

        // ── Per-shot counters ──────────────────────────────────────────────────
        public int KolShoot;     // increments each shot — used for sndShoot_n gating

        /// <summary>
        /// Per-<b>instance</b> damage multiplier, applied to the shot's damage on top of everything the
        /// definition and the ammo say. <c>1</c> is the identity and the value every weapon has unless
        /// something explicitly stamps it.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this has to live on the state and not on the definition.</b>
        /// <c>WeaponDefinition</c> is a <c>ScriptableObject</c> shared by every unit holding that weapon
        /// id, so writing a multiplier into it would double the damage of <i>every</i> gutsy in the game
        /// rather than the one variant that earned it. AS3 can mutate its weapon freely because each
        /// <c>Weapon.create</c> builds a fresh object
        /// (<c>UnitGutsy.as:30-33</c>: <c>this.dopWeapon.damage *= 2</c>), which is exactly the
        /// per-instance semantics this field reproduces.</para>
        ///
        /// <para><b>Consumed by <c>RangedWeaponController</c> only.</b> That is the controller
        /// <c>robofire</c> resolves to (<c>tip='0'</c>, <c>AllData.txt:1438</c>), and it is the only
        /// weapon in the game whose damage an AS3 variant class doubles. Folding it into melee, thrown
        /// and magic as well would be inventing behaviour nobody asked for; a controller that ignores
        /// the field is honest, and the doc says so.</para>
        /// </remarks>
        public float DamageScale = 1f;

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
        /// AS3 <c>WClub.blumR</c> — a rotation offset in <b>degrees</b> added to the <i>drawn</i>
        /// angle only (<c>WClub.as:332</c> computed, <c>:747</c> applied as
        /// <c>vis.rotation = rot*180/PI + blumR</c>).
        ///
        /// <para><b>It must not touch <see cref="Rot"/>.</b> <c>rot</c> is what the blade tip is
        /// projected along, so folding a cosmetic lean into it would move the hit volume. Zero for
        /// every non-melee weapon and for a rigidly mounted (<c>weaponKrep &gt; 0</c>) one, which never
        /// runs the spring that produces it.</para>
        /// </summary>
        public float BlumRDeg;

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

        public WeaponRuntimeState(IWeaponStats def)
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
        /// Durability as a 0–1 fraction past the halfway point — AS3 <c>Weapon.breaking</c>. Thin
        /// delegation to <see cref="WeaponWearMath.Breaking"/>, which is where the formula lives so it
        /// can be executed offline; this method exists because the state is the natural place for a
        /// caller to ask the question. The three consumers are the ranged spread, the jam chance and
        /// (through the two damage multipliers) the damage itself.
        /// </summary>
        public float Breaking() => WeaponWearMath.Breaking(Def.maxDurability, CurrentDurability);

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
