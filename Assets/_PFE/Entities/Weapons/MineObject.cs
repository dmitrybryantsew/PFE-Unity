using System;
using System.Collections.Generic;
using UnityEngine;
using VContainer;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Combat;
using PFE.Systems.Weapons;

namespace PFE.Entities.Weapons
{
    /// <summary>
    /// Placed mine — AS3 <c>fe/unit/Mine.as</c>, placed by the <c>throwTip == 1</c> branch of
    /// <c>WThrow.shoot()</c> (<c>WThrow.as:142-174</c>).
    ///
    /// <para><b>The mine is a Unit in AS3, and this mirrors the parts of that which matter.</b> Its
    /// per-frame behaviour is <c>Mine.control()</c> (<c>Mine.as:290-358</c>), which is three states in
    /// a fixed order: <i>arming</i> (<c>reloadTime &gt; 0</c>, which returns before anything else
    /// runs), <i>armed</i> (<c>aiState == 1</c>, the proximity scan), and <i>counting down</i>
    /// (<c>aiState == 2</c>, <c>explTime</c> ticking to 0). The states are two independent fields in
    /// the oracle, not one enum, and that is load-bearing: <c>activate()</c> can be called while the
    /// mine is still arming, and the oracle then sets <c>aiState = 2</c> but keeps returning early
    /// until the arming finishes. Collapsing them into one enum would either detonate an un-armed
    /// mine or silently drop the radio signal, depending on which way it was collapsed.</para>
    ///
    /// <para><b>Timers are in seconds, converted once from the AS3 frame counts.</b> The durations
    /// were already right — <c>armingFrames / 30</c> is 2.5 s on both clocks — so the frame counts are
    /// divided by <see cref="SimClock.FramesPerSecond"/> at the point they are read and nothing else
    /// here is a frame count. The one exception is the proximity <i>cadence</i>: AS3 scans on
    /// <c>aiN % 4 == 0</c> (<c>:311</c>), which is 4/30 s, and a per-frame <c>% 4</c> on a 50 Hz
    /// <c>FixedUpdate</c> would be a different rate. It is therefore an accumulator in seconds, not a
    /// frame counter.</para>
    ///
    /// <para><b>What this file deliberately does not model, and why.</b>
    /// <list type="bullet">
    /// <item><c>Unit.activateTrap</c> (<c>Unit.as:430</c>, default <c>2</c>) is not imported, so the
    /// two guard branches that read it — the <c>== 0</c> skip and the <c>== 1 &amp;&amp; …</c>
    /// half-skip (<c>Mine.as:315</c>) — collapse to the oracle's own default value. They are written
    /// out as named, commented branches below rather than deleted, because the import slot is what is
    /// missing, not the rule.</item>
    /// <item><c>otschet</c> (<c>:319</c>, the 10-frame grace a trap-lvl-1 player gets) hangs off the
    /// same unimported field, so it is likewise unreachable. It is <b>not</b> modelled as a live field
    /// — an unused counter that reads like an implemented grace period is the "dead code that looks
    /// wired" failure, and the branch is one line to add once <c>activateTrap</c> is imported. See
    /// <see cref="MayTrigger"/>.</item>
    /// <item>The mine's <c>damage1 *= Math.random() * 0.3 + 0.85</c> (<c>Mine.as:171</c>) and the
    /// <c>sapper</c> perk's bonus (<c>WThrow.as:169-173</c>) are weapon-skill/perk work and belong
    /// with the plan named in <c>docs/RPG_Skill_Fix_Plan.md</c>; see the audit for the recorded
    /// gap.</item>
    /// <item><c>destroy</c> (tile destruction) and the knockback impulse. The port's
    /// <see cref="PendingDamage.Explosion"/> carries neither, and the mine's blast is the same
    /// <c>Unit.explosion</c> shape a grenade uses — so both are the blast path's gap, not the mine's,
    /// and are recorded in <c>docs/AUDIT_throwable_and_explosive_2026-10-03.md</c>.</item>
    /// </list></para>
    ///
    /// <para><b>Friendly fire: a mine hits its own side, and that is the oracle.</b> The blast is
    /// built by <c>Unit.explosion()</c> (<c>Unit.as:3328-3349</c>) as
    /// <c>new Bullet(this, X, Y - 3, null, …)</c> — <c>weap</c> is never assigned, and every
    /// friendly-fire gate in <c>Bullet.explGas</c>/<c>explBlast</c> is guarded by
    /// <c>if(this.weap &amp;&amp; …)</c> (<c>:764</c>, <c>:817</c>). So the ×0.25 and the
    /// <c>pers.autoExpl</c> scaling both short-circuit, and a mine damages the player who placed it at
    /// full strength. Passing a faction multiplier here would invent a rule the oracle does not
    /// have.</para>
    /// </summary>
    [RequireComponent(typeof(Collider2D))]
    public sealed class MineObject : MonoBehaviour, IDamageable
    {
        // ── Static registry for radio detonation ─────────────────────────────

        private static readonly List<MineObject> _allMines = new();

        /// <summary>
        /// Radio detonation — AS3 <c>WThrow.detonator()</c> (<c>WThrow.as:297-312</c>), called from the
        /// RELOAD key (<c>UnitPlayer.as:2358</c>). Iterates every live mine whose id matches and calls
        /// <c>activate()</c>.
        ///
        /// <para><b>No arming filter, and the previous one was an invention.</b> The oracle's loop has
        /// exactly two conditions — <c>_loc1_ is Mine</c> and <c>_loc1_.id == id</c> — so a mine that
        /// is still arming is detonated too (it sets <c>aiState = 2</c> and the countdown then waits
        /// out the remaining arming frames, because <c>control()</c> returns early until
        /// <c>reloadTime</c> expires). The port used to require <c>_isArmed</c>, which silently
        /// swallowed a press made within 2.5 s of placing.</para>
        ///
        /// <para>Iterates downward because <see cref="Activate"/> can, on a later frame, remove an
        /// entry — and mutating a list mid-iteration is the classic silent-skip.</para>
        /// </summary>
        public static void DetonateAll(string weaponId)
        {
            for (int i = _allMines.Count - 1; i >= 0; i--)
            {
                var mine = _allMines[i];
                if (mine != null && mine._weaponId == weaponId)
                    mine.Activate();
            }
        }

        // ── Constants ─────────────────────────────────────────────────────────

        private const float PpuScale = 100f;

        /// <summary>AS3 <c>Mine.as:311</c> — the proximity scan runs on <c>aiN % 4 == 0</c>.</summary>
        private const int SenseIntervalFrames = 4;

        /// <summary>AS3 <c>WThrow.as:155</c> — the placed mine's countdown is <c>explTime *= 0.3</c>.</summary>
        private const float FuseScale = 0.3f;

        /// <summary>
        /// AS3 <c>Mine.explTime</c> declaration default (<c>Mine.as:20</c>). Used only if the data
        /// carries no <c>&lt;char time&gt;</c>; every one of the eight mine rows carries
        /// <c>time='15'</c>.
        /// </summary>
        private const int DefaultFuseFrames = 15;

        /// <summary>
        /// AS3 <c>Mine.sens</c> declaration default (<c>Mine.as:24</c>). Used only if the data carries
        /// no <c>&lt;char sens&gt;</c>; seven of the eight mine rows carry <c>sens='100'</c> and
        /// <c>x37</c> carries <c>0</c>, which is the oracle's "never proximity-trigger".
        /// </summary>
        private const float DefaultSensPx = 100f;

        /// <summary>
        /// AS3 <c>Mine.as:317</c> — the sensing band is <c>|dx| &lt; sens</c> horizontally but
        /// <c>dy ∈ (-sens, +0.4·sens)</c> vertically, in AS3's Y-<b>down</b> space. Flipped into the
        /// port's Y-up space that is <c>sens</c> above the mine and <c>0.4·sens</c> below.
        /// </summary>
        private const float SensDownFraction = 0.4f;

        // ── Configuration (set at spawn) ──────────────────────────────────────

        private string _weaponId;
        private float  _explRadius;    // Unity units
        private float  _sens;          // Unity units (AS3 px / 100)
        private float  _maxHp;
        private float  _hp;

        // ── Timers (seconds) ──────────────────────────────────────────────────

        /// <summary>AS3 <c>reloadTime</c> — the arming delay. <c>WThrow.as:157</c> hardcodes 75.</summary>
        private float _armingTimer;

        /// <summary>
        /// AS3 <c>explTime</c> — the countdown, already scaled by <see cref="FuseScale"/> at placement.
        /// Only ticked in the <c>aiState == 2</c> branch, exactly as the oracle does.
        /// </summary>
        private float _explTimer;

        /// <summary>Seconds until the next proximity scan — the seconds form of <c>aiN % 4</c>.</summary>
        private float _senseTimer;

        /// <summary>
        /// AS3 <c>aiState</c>. <c>1</c> = armed and scanning, <c>2</c> = activated and counting down.
        /// Kept separate from <see cref="_armingTimer"/> for the reason in the class note.
        /// </summary>
        private int _aiState = 1;

        // ── Damage ────────────────────────────────────────────────────────────

        private bool          _hasDamageContext;
        private DamageContext _damageContext;

        /// <summary>
        /// Faction of the unit that placed this mine — AS3 <c>_loc5_.fraction = owner.fraction</c>
        /// (<c>WThrow.as:149</c>), carried in through the damage context. It is both the
        /// <c>_loc1_.fraction == fraction</c> half of the proximity guard and the reason the mine does
        /// not trigger on its own placer.
        /// </summary>
        private FactionType _ownerFaction = FactionType.Neutral;

        /// <summary>
        /// Set by the damage path, consumed by <see cref="LateUpdate"/>. A damage drain may not
        /// detonate a mine inline: <see cref="Detonate"/> runs an overlap query and reports more
        /// damage, and doing that while the queue is being drained is exactly the re-entrancy the
        /// deferred-report design exists to prevent.
        /// </summary>
        private bool _pendingDetonate;

        private bool _detonated;

        // ── Injected ─────────────────────────────────────────────────────────

#pragma warning disable CS0649
        [Inject] private DamageSystem _damageSystem;
#pragma warning restore CS0649

        public Action<MineObject> OnReturnToPool { get; set; }

        // ── Registry lifecycle ────────────────────────────────────────────────

        private void OnEnable()  => _allMines.Add(this);
        private void OnDisable() => _allMines.Remove(this);

        private void Awake()
        {
            // The prefab ships with `m_IsTrigger: 0` and no Rigidbody2D, which makes the mine a solid
            // obstacle that cannot be detected as an overlap volume at all. Both are forced here
            // rather than in the asset so a re-import cannot silently undo the fix.
            var collider = GetComponent<Collider2D>();
            if (collider != null) collider.isTrigger = true;

            if (GetComponent<Rigidbody2D>() == null)
            {
                var rb = gameObject.AddComponent<Rigidbody2D>();
                rb.bodyType    = RigidbodyType2D.Kinematic;
                rb.gravityScale = 0f;
            }
        }

        // ── Initialization ────────────────────────────────────────────────────

        /// <summary>Called by <see cref="ProjectileSpawner"/> from a Mine ShotPlan.</summary>
        /// <param name="weaponId">The placing weapon's id — the key <see cref="DetonateAll"/> matches on.</param>
        /// <param name="explRadius">Blast radius in <b>Unity units</b> (<c>&lt;char expl&gt;</c> px / 100).</param>
        /// <param name="fuseFrames">
        /// <c>&lt;char time&gt;</c> — the same attribute the thrown fuse uses. AS3 takes it as the mine's
        /// <c>explTime</c> and then scales it by 0.3 at placement, so this is <i>not</i> the countdown
        /// itself.
        /// </param>
        /// <param name="armingFrames">AS3 <c>reloadTime</c>. <c>WThrow.as:157</c> hardcodes 75.</param>
        /// <param name="sensPx"><c>&lt;char sens&gt;</c> in AS3 pixels. <c>0</c> disables the scan entirely.</param>
        /// <param name="maxHp"><c>&lt;char maxhp&gt;</c> — AS3 <c>hp = maxhp = @maxhp</c> (<c>Mine.as:134-137</c>).</param>
        public void Initialize(
            string weaponId,
            float  explRadius,
            int    fuseFrames,
            int    armingFrames,
            float  sensPx,
            int    maxHp)
        {
            _weaponId   = weaponId;
            _explRadius = explRadius;

            int fuse = fuseFrames > 0 ? fuseFrames : DefaultFuseFrames;
            _explTimer = fuse * FuseScale / SimClock.FramesPerSecond;

            _armingTimer = armingFrames / SimClock.FramesPerSecond;

            // 0 is meaningful — it is x37's "never proximity-trigger" — so only a negative falls back
            // to the declaration default. An absent attribute is already 100 by way of the importer.
            _sens = (sensPx >= 0f ? sensPx : DefaultSensPx) / PpuScale;

            _maxHp = maxHp > 0 ? maxHp : 10f;
            _hp    = _maxHp;

            _aiState        = 1;
            _senseTimer     = SenseIntervalFrames / SimClock.FramesPerSecond;
            _detonated      = false;
            _pendingDetonate = false;
        }

        public void SetDamageContext(DamageContext ctx)
        {
            _damageContext    = ctx;
            _hasDamageContext = true;
            _ownerFaction     = ctx.OwnerFaction;
        }

        // ── Unity lifecycle ───────────────────────────────────────────────────

        /// <summary>
        /// One step of <c>Mine.control()</c> (<c>Mine.as:290-358</c>), in the oracle's order.
        ///
        /// <para>The early <c>return</c> while arming is the whole reason the two state fields are
        /// separate: the oracle's arming branch returns <b>before</b> the countdown and before the
        /// <c>explTime &lt;= 0</c> death test, so an un-armed mine cannot explode even if
        /// <c>activate()</c> was called.</para>
        /// </summary>
        private void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            Step(dt);
        }

        private void Step(float dt)
        {
            if (_detonated) return;

            // AS3 `if(this.reloadTime > 0) { --reloadTime; …; return; }`
            if (_armingTimer > 0f)
            {
                _armingTimer -= dt;
                return;
            }

            if (_aiState == 1)
            {
                // AS3 `aiState == 1 && oduplenie <= 0 && this.sens > 0` — the third term is why x37
                // (sens='0') never triggers on proximity and needs its radio detonator.
                if (_sens > 0f)
                {
                    _senseTimer -= dt;
                    if (_senseTimer <= 0f)
                    {
                        _senseTimer += SenseIntervalFrames / SimClock.FramesPerSecond;
                        Scan();
                    }
                }
            }
            else if (_aiState == 2)
            {
                // AS3 decrements here and tests afterwards, outside the branch — so the countdown is
                // what kills it, but the test itself runs every frame.
                _explTimer -= dt;
            }

            if (_explTimer <= 0f)
                _pendingDetonate = true;
        }

        /// <summary>
        /// The view/late pass. Also where a damage-reported detonation is honoured, because a tick may
        /// not run an overlap query and report further damage.
        /// </summary>
        private void LateUpdate()
        {
            if (_pendingDetonate)
            {
                _pendingDetonate = false;
                Detonate();
            }
        }

        // ── Activation ────────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>Mine.activate()</c> (<c>Mine.as:277-283</c>): <c>aiState = 2</c>, show the vis, play
        /// it. Called by the proximity scan and by the radio detonator.
        ///
        /// <para>Idempotent: activating an already-counting mine must not restart the fuse, and the
        /// oracle's <c>aiState = 2</c> is likewise a no-op the second time.</para>
        /// </summary>
        public void Activate()
        {
            if (_detonated || _aiState == 2) return;
            _aiState = 2;
        }

        // ── Proximity ─────────────────────────────────────────────────────────

        /// <summary>
        /// AS3's unit loop (<c>Mine.as:313-331</c>): the guard, then the ellipse, then activate.
        /// </summary>
        private void Scan()
        {
            if (_sens <= 0f) return;

            Vector3 minePos = transform.position;

            // AS3 iterates `loc.units`; the port's units are colliders in one scene. A circle of
            // radius `sens` is a superset of the ellipse, so it cannot miss a candidate — the exact
            // band test below is what decides.
            Collider2D[] hits = Physics2D.OverlapCircleAll(minePos, _sens);
            foreach (var hit in hits)
            {
                if (hit.isTrigger) continue;

                var unit = hit.GetComponent<UnitController>();
                if (unit == null || !MayTrigger(unit)) continue;

                Vector3 p = unit.transform.position;
                float dx = p.x - minePos.x;
                float dy = p.y - minePos.y;

                // AS3, in its Y-down space: |dx| < sens and dy ∈ (-sens, +0.4·sens). Unity's Y is up,
                // so the band flips to (-0.4·sens, +sens) — sens above the mine, 0.4·sens below.
                if (!(dx < _sens && dx > -_sens &&
                      dy < _sens * SensDownFraction && dy > -_sens))
                    continue;

                Activate();
                return;
            }
        }

        /// <summary>
        /// The oracle's guard, transcribed from the single negative conjunction at
        /// <c>Mine.as:315</c>. Each clause is named; the two that read <c>activateTrap</c> are kept as
        /// comments because the <i>import</i> is what is missing, not the rule.
        /// </summary>
        private bool MayTrigger(UnitController unit)
        {
            if (unit == null) return false;

            // AS3 `_loc1_.activateTrap == 0` → skip.
            //   activateTrap is not imported; AS3's declaration default is 2 (Unit.as:430), which is
            //   not 0, so the clause is false for every unit in the port.
            //
            // AS3 `_loc1_.activateTrap == 1 && fraction != F_PLAYER && _loc1_.fraction != F_PLAYER`
            //   → skip. Same unimported field, same default, so also false.
            //
            // Both are recorded gaps, not deleted rules. See the class note.

            // AS3 `isMeet(_loc1_)` (Unit.as:4522-4525): not null, same room, not disabled, not
            // trigDis, `sost != 4`, and not itself. The port has one room per scene, and a destroyed
            // or disabled unit is not alive.
            if (!unit.IsAlive) return false;

            // AS3 `_loc1_.sost == 3` → skip. `sost` is the oracle's per-unit state machine; the port
            // models only its terminal outcome, so a dead unit is the reachable case and IsAlive above
            // already covers it.

            // AS3 `_loc1_.fraction == fraction` → skip. This is also what keeps a mine from
            // triggering on the unit that placed it: `_loc5_.fraction = owner.fraction`
            // (WThrow.as:149).
            if (unit.Faction == _ownerFaction) return false;

            // AS3 `_loc1_.fraction == 0` → skip. A neutral never trips a mine.
            if (unit.Faction == FactionType.Neutral) return false;

            return true;
        }

        // ── Detonation ────────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>Mine.die()</c> → <c>Unit.die</c> → <c>exterminate</c> → <c>Mine.dropLoot()</c>
        /// (<c>Mine.as:256-259</c>), which is <c>explosion(damage1, tipDamage, explRadius, 0, …)</c>.
        ///
        /// <para><c>damage1</c> is <c>&lt;char damexpl&gt;</c>, which is
        /// <see cref="DamageContext.ExplosionDamage"/> — not <c>BaseDamage</c>. The falloff and the
        /// formula live in <see cref="DamageSystem"/>; this only enumerates the overlap and reports.</para>
        ///
        /// <para><b><c>factionMultiplier: 1f</c>, deliberately.</b> See the class note: a mine's blast
        /// is built with <c>weap == null</c>, so the oracle's friendly-fire gate short-circuits and a
        /// mine hurts its own side at full strength.</para>
        /// </summary>
        public void Detonate()
        {
            if (_detonated) return;
            _detonated = true;

            Vector3 centre = transform.position;

            if (_explRadius > 0f)
            {
                Collider2D[] hits = Physics2D.OverlapCircleAll(centre, _explRadius);
                foreach (var hit in hits)
                {
                    if (hit.isTrigger) continue;

                    var damageable = hit.GetComponent<IDamageable>();
                    if (damageable == null || !damageable.IsAlive) continue;
                    if (ReferenceEquals(damageable, this)) continue;

                    if (_hasDamageContext && _damageSystem != null)
                    {
                        _damageSystem.Report(PendingDamage.Explosion(
                            _damageContext, damageable,
                            hit.transform.position, centre, _explRadius,
                            factionMultiplier: 1f));
                    }
                    else
                    {
                        // Headless / no-resolver fallback. Not a rule — the resolver owns the number.
                        damageable.TakeDamage(10f);
                    }
                }
            }

            ReturnToPool();
        }

        // ── Pool support ─────────────────────────────────────────────────────

        private void ReturnToPool()
        {
            if (OnReturnToPool != null) OnReturnToPool(this);
            else Destroy(gameObject);
        }

        public void ResetMine()
        {
            _detonated        = false;
            _pendingDetonate  = false;
            _hasDamageContext = false;
            _aiState          = 1;
            _armingTimer      = 0f;
            _explTimer        = 0f;
            _senseTimer       = 0f;
            _explRadius       = 0f;
            _sens             = 0f;
            _hp               = 0f;
            _maxHp            = 0f;
            _weaponId         = null;
            _ownerFaction     = FactionType.Neutral;
        }

        // ── IDamageable ───────────────────────────────────────────────────────
        // The oracle's mine is a Unit with hp = maxhp = <char maxhp> (10 on all eight rows), so it can
        // be shot and destroyed before it fires. Everything else here is the AS3 declaration default
        // for a mine: no armour, no skin, no evasion, fixed in place.

        /// <summary>
        /// AS3 <c>Mine.as:190-191</c> — <c>vulner[D_EMP] = 1; vulner[D_VENOM] = 0</c>. Note this is
        /// the <b>inverse</b> of AS3's unit default (which forces <c>emp = 0</c>): a mine <i>is</i>
        /// EMP-vulnerable and venom-immune.
        /// </summary>
        public VulnerabilityData Vulnerabilities
        {
            get
            {
                var v = VulnerabilityData.Neutral;   // every slot 1, emp 0
                v.emp   = 1f;                        // Mine.as:190
                v.venom = 0f;                        // Mine.as:191
                return v;
            }
        }

        public ArmourState Armour => ArmourState.None;

        /// <summary>AS3 <c>Unit.skin</c> — a mine declares none, so 0.</summary>
        public float SkinResistance => 0f;

        /// <summary>
        /// AS3 <c>Unit.dexter</c> defaults to 1 (<c>Unit.as:166</c>), which is
        /// <see cref="EvasionState.Default"/>. A mine is not dodging anything.
        /// </summary>
        public EvasionState Evasion => EvasionState.Default;

        /// <summary>
        /// AS3 <c>Unit.knocked</c>. A placed mine is not thrown by a blast: the port has no
        /// Rigidbody2D driving it and <see cref="ApplyKnockback"/> is a no-op, so answering <c>0</c>
        /// ("cannot be moved", AS3's own encoding for turrets and fixed units) is the honest value
        /// rather than a default that would compute an impulse nothing consumes.
        /// </summary>
        public float Knocked => 0f;

        public float Mass => 1f;

        public bool IsInvulnerable => false;

        /// <summary>
        /// AS3 <c>Mine.as:188</c> sets <c>doop = true</c>, so a mine is one of the non-living classes
        /// and suppresses the stealth crit.
        /// </summary>
        public bool IsNonLiving => true;

        public float CurrentHealth => _hp;
        public float MaxHealth     => _maxHp > 0f ? _maxHp : 1f;
        public bool  IsAlive       => !_detonated && _hp > 0f;

        /// <summary>
        /// Raw HP subtraction, bypassing armour — used by the no-resolver fallback in
        /// <see cref="Detonate"/> and by test doubles. A mine has no armour to bypass anyway.
        /// </summary>
        public void TakeDamage(float damage)
        {
            if (_detonated || damage <= 0f) return;

            _hp -= damage;
            if (_hp <= 0f) _pendingDetonate = true;
        }

        /// <summary>
        /// The resolver's write. AS3 <c>Mine.die()</c> is reached as soon as <c>hp</c> runs out, so a
        /// lethal hit arms the detonation; it is honoured in <see cref="LateUpdate"/> rather than
        /// inline, because this runs inside a damage drain.
        /// </summary>
        /// <returns><c>false</c> always — a mine has no armour, so it can never break any.</returns>
        public bool ApplyDamage(in DamageOutcome outcome)
        {
            if (_detonated) return false;

            if (outcome.HpDamage > 0f)
            {
                _hp -= outcome.HpDamage;
                if (_hp <= 0f) _pendingDetonate = true;
            }

            return false;
        }

        /// <summary>
        /// No-op. The mine is a fixed emplacement with no Rigidbody2D integration — see
        /// <see cref="Knocked"/>, which returns 0 so the resolver produces no impulse to begin with.
        /// </summary>
        public void ApplyKnockback(Vector2 impulse) { }

        // ── Debug ─────────────────────────────────────────────────────────────

        private void OnDrawGizmos()
        {
            if (_explRadius > 0f)
            {
                Gizmos.color = _aiState == 2
                    ? new Color(1f, 0.2f, 0f, 0.3f)
                    : new Color(1f, 1f, 0f, 0.2f);
                Gizmos.DrawWireSphere(transform.position, _explRadius);
            }

            if (_sens > 0f)
            {
                // The sensing band: sens above, 0.4*sens below — the flipped AS3 ellipse.
                Gizmos.color = new Color(0f, 1f, 0f, 0.15f);
                Gizmos.DrawWireCube(
                    transform.position + new Vector3(0f, _sens * (1f - SensDownFraction) * 0.5f, 0f),
                    new Vector3(_sens * 2f, _sens * (1f + SensDownFraction), 0f));
            }
        }
    }
}
