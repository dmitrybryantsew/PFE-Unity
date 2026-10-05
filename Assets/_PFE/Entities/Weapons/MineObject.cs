using System;
using System.Collections.Generic;
using UnityEngine;
using VContainer;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Audio;
using PFE.Systems.Combat;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Physics;
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

        /// <summary>
        /// The placing weapon's <c>&lt;snd sens&gt;</c> — the arming beep, AS3 <c>Mine.sndSens</c>
        /// (<c>Mine.as:118</c>, played at <c>:340-343</c>). Empty for every non-mine weapon, and the
        /// beep is skipped rather than substituted, exactly as the oracle's own <c>!= ""</c> guard does.
        /// </summary>
        private string _soundSens;

        // ── The beep cadence ──────────────────────────────────────────────────

        /// <summary>AS3 <c>Mine.as:340</c> — <c>explTime % 5 == 3</c>, the beep's period in ticks.</summary>
        public const int BeepPeriodTicks = 5;

        /// <summary>AS3 <c>Mine.as:340</c> — the phase within <see cref="BeepPeriodTicks"/>.</summary>
        public const int BeepPhaseTick = 3;

        /// <summary>
        /// AS3 <c>Mine.as:340</c> — <c>if(this.sndSens != "" &amp;&amp; this.explTime % 5 == 3)</c>:
        /// whether the beep sounds on a tick with <paramref name="remainingTicks"/> left on the fuse.
        ///
        /// <para><b>Public and static so the cadence is pinned by fixtures.</b> Reached only through a
        /// running mine otherwise, and "how often does it beep" is precisely the kind of question a
        /// play-test answers wrongly — a beep every 5 ticks and a beep every 4 both sound like beeping.
        /// C# <c>%</c> keeps the sign of the dividend exactly as AS3's does, and the caller clamps the
        /// countdown at zero, so the negative case cannot arise.</para>
        /// </summary>
        public static bool ShouldBeep(int remainingTicks)
            => remainingTicks % BeepPeriodTicks == BeepPhaseTick;

        // ── Timers (seconds) ──────────────────────────────────────────────────

        /// <summary>AS3 <c>reloadTime</c> — the arming delay. <c>WThrow.as:157</c> hardcodes 75.</summary>
        private float _armingTimer;

        /// <summary>
        /// AS3 <c>explTime</c> — the countdown, already scaled by <see cref="FuseScale"/> at placement.
        /// Only ticked in the <c>aiState == 2</c> branch, exactly as the oracle does.
        /// </summary>
        private float _explTimer;

        /// <summary>
        /// The same countdown as <see cref="_explTimer"/>, but as an <b>integer tick count</b>.
        ///
        /// <para><b>Two representations of one value, and the integer is the oracle's.</b> AS3's
        /// <c>explTime</c> is an <c>int</c> (<c>Mine.as:20</c>) that <c>WThrow.as:155</c> scales by
        /// <c>0.3</c> — <b>truncating</b>, so <c>time='15'</c> becomes 4, not 4.5. The beep test is
        /// <c>explTime % 5 == 3</c> (<c>:340</c>), a modulo that only means anything on the integer.
        /// A float seconds timer can be asked for the same thing only by rounding, and the rounding
        /// error would move the beep by a tick — so the integer is carried separately and this is the
        /// one field the cadence reads.</para>
        /// </summary>
        private int _explTicks;

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

        // ── Visual ────────────────────────────────────────────────────────────
        //
        // AS3's mine is a Unit and always has a view: `Mine.as:98` builds it as
        // `Res.getVis("vis" + id, vismine)` — the same `vis<weaponId>` symbol the throwing weapon
        // itself uses — and then drives it through THREE states, in `control()`'s own order:
        //
        //   arming      `Mine.as:298-303`  `reloadTime > 0`, and `gotoAndStop(2)` is fired on the
        //                                  frame `reloadTime` reaches 1 — i.e. frame 1 (index 0)
        //                                  until the last arming frame, then frame 2 (index 1).
        //   armed       `Mine.as:301`      static frame 2 (index 1) while the proximity scan runs.
        //   counting    `Mine.as:282`      `activate()` calls `vis.play()`, so the timeline loops —
        //                                  the blink that tells you it is about to go off.
        //
        // Placement makes it visible immediately (`WThrow.as:158-159` `gotoAndStop(1); setVis(true)`),
        // so `setVis(false)`'s alpha-0.1 hidden state is never the state you see a placed mine in.
        //
        // `Assets/_PFE/Prefabs/Mine.prefab` has no renderer on itself or any child, so none of this
        // was drawn: the mine armed, scanned, blinked and exploded entirely invisibly.

        [Header("Visual")]
        [Tooltip("Sprite renderer for the mine. Leave empty to have one created on a child at runtime " +
                 "(the prefab ships without one).")]
        [SerializeField] private SpriteRenderer _visualRenderer;

        [Tooltip("Sorting layer for the created child renderer. Matches the projectile template " +
                 "(ProjectileTemplateSpec.SortingLayerName), which is on " +
                 "Foreground; the project's 'Default' layer sits BELOW the map tiles, so leaving this " +
                 "empty draws the mine behind the floor.")]
        [SerializeField] private string _visualSortingLayer = MapSortingLayers.Foreground;

        private Transform _visualTransform;
        private WeaponVisualDefinition _currentVisual;
        private float _visualFrameTimer;
        private int   _visualFrameIndex;

        /// <summary>Flash frame 2 — the armed state (`Mine.as:301`).</summary>
        private const int ArmedFrameIndex = 1;

        /// <summary>Name of the child that carries the sprite, created on demand.</summary>
        public const string VisualChildName = "visual";

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

        // ── Body (AS3: a mine IS a Unit, so `Unit.step()` integrates it) ──────────────────────
        //
        // The mine used to be modelled as a fixed emplacement: a Kinematic body with gravityScale 0
        // and no position integration at all. That is not what the oracle does. `WThrow.as:142-174`
        // does `new Mine(id)` and `loc.units.push(_loc5_)`, so the mine joins the ordinary unit loop;
        // `Mine.as:178` sets `isFly = false` and `grav` keeps its `Unit.as:256` default of 1, so the
        // non-fly branch of `Unit.as` runs and `:1962-1964` applies `dy += World.ddy * grav`. The
        // mine therefore FALLS from the hand to the floor — it only stops being a physics object
        // when the placement retry finds it spawned inside geometry and sets `fixed = true`
        // (`WThrow.as:171`), which is the rare case, not the normal one.
        //
        // The step below mirrors `UnitController` rather than inventing a second model: ground probe
        // from the tile query, `UnitFallPhysics.FallSpeed`, `GroundBrake`, then one position write.
        // The one thing it does NOT share is `UnitController`'s prop/shelf support — a mine resting
        // on a crate is a recorded gap, not an oversight (see the class note).

        /// <summary>AS3 <c>Unit.dx/dy</c>, in Unity units per second.</summary>
        private Vector2 _velocity;

        /// <summary>
        /// AS3 <c>Unit.fixed</c> — set only by the placement retry (<c>WThrow.as:171</c>) when the
        /// mine spawns inside a tile. Gates the position write exactly as the oracle's
        /// <c>if(!this.fixed) run()</c> does (<c>Unit.as:1809</c>): gravity still accumulates, the
        /// move is simply never applied.
        /// </summary>
        private bool _pinned;

        /// <summary>AS3 <c>isLaz</c>, cached from the last <see cref="StepBody"/> — see <see cref="IsGrounded"/>.</summary>
        private bool _grounded;

        /// <summary>This object's own collider — the box the ground probe and the placement retry read.</summary>
        private Collider2D _bodyCollider;

        /// <summary>Kinematic mover. See <see cref="Awake"/> for why it is not Box2D-gravity driven.</summary>
        private Rigidbody2D _body;

        /// <summary>
        /// The room's tile query — AS3's <c>loc.space</c> lookups, i.e. <c>Unit.collisionAll()</c>
        /// (<c>Unit.as:2541</c>) and <c>isLaz</c> (<c>Unit.as:1962</c>). Handed over by
        /// <see cref="ProjectileSpawner"/> because a prefab instance never goes through the
        /// container. Null is legal and means "no room": the mine then simply does not fall, which
        /// is the same reduced answer <c>UnitController</c> gives a unit with no room.
        /// </summary>
        private ITileQueryService _tileQuery;

        // ── Explosion presentation (the blast a mine draws when it goes off) ──────────────────
        //
        // AS3's mine explodes through `Unit.explosion()` (`Unit.as:3328`), which does NOT draw the
        // blast itself — it constructs a `Bullet` at the mine's position, sets `weapId = this.id` so the
        // bullet resolves the placing weapon's `visexpl`, and calls the bullet's `explosion()` →
        // `explRun()` → `explVis()` (`Bullet.as:665,705,878`). So a mine's blast goes through exactly the
        // same `explVis()` the port already mirrors for projectiles and thrown objects — which is why
        // these two inputs are the placing weapon's, not the mine's own.

        /// <summary>The placing weapon's <c>vis.@visexpl</c> — what <c>explVis()</c> reads first.</summary>
        private string     _visExpl;

        /// <summary>Selects the fallback arm of the explosion table when <see cref="_visExpl"/> is empty.</summary>
        private DamageType _damageType;

        /// <summary>Scratch list for the planned emits — reused so a burst allocates nothing.</summary>
        private readonly List<PFE.Systems.Particles.ParticleEmit> _explosionEmits =
            new List<PFE.Systems.Particles.ParticleEmit>();

        /// <summary>The unseeded presentation stream, resolved once (see <see cref="PresentationRng"/>).</summary>
        private PFE.Core.Rng.IRngService _presentationRng;

        // ── Injected ─────────────────────────────────────────────────────────

#pragma warning disable CS0649
        [Inject] private DamageSystem _damageSystem;

        // The blast's presentation — the same trio Projectile injects. All optional: a fixture built
        // with `new` has none, and the emit simply does nothing.
        [Inject] private ISoundService                                       _soundService;
        [Inject] private PFE.Systems.Particles.Adapters.RoomParticleEmitter _particles;
        [Inject] private PFE.Systems.Particles.IParticleTileWater            _particleTileWater;
        [Inject] private PFE.Core.Rng.IRngService                            _rng;
#pragma warning restore CS0649

        public Action<MineObject> OnReturnToPool { get; set; }

        // ── Registry lifecycle ────────────────────────────────────────────────

        private void OnEnable()  => _allMines.Add(this);
        private void OnDisable() => _allMines.Remove(this);

        private void Awake() => EnsureBody();

        /// <summary>
        /// <see cref="Awake"/>'s body wiring — idempotent, and deliberately callable after Awake.
        ///
        /// <para><b>Why this is not just Awake's body.</b> Unity does not run <c>Awake</c> for a plain
        /// <c>AddComponent</c> outside play mode, so an EditMode fixture that builds a mine would step
        /// a body with no collider and no mover. That is the "the arrange could not run" failure — a
        /// test that goes green because nothing happened. Resolving lazily costs one null check per
        /// step and makes the component constructible in any order.</para>
        /// </summary>
        private void EnsureBody()
        {
            if (_bodyCollider == null)
            {
                // The prefab ships with `m_IsTrigger: 0`, which makes the mine a solid obstacle that
                // cannot be detected as an overlap volume at all. Forced here rather than in the asset
                // so a re-import cannot silently undo the fix.
                _bodyCollider = GetComponent<Collider2D>();
                if (_bodyCollider != null) _bodyCollider.isTrigger = true;
            }

            if (_body == null)
            {
                _body = GetComponent<Rigidbody2D>();
                if (_body == null) _body = gameObject.AddComponent<Rigidbody2D>();

                // Kinematic and gravity-free BY CONSTRUCTION, and that is the oracle's model rather
                // than a shortcut: AS3 integrates `dy += World.ddy` itself and this port does the same
                // through UnitFallPhysics / MineBodyMath, writing the result itself — the same
                // integration UnitController runs for every other unit. Leaving Box2D's gravity on
                // would add a second acceleration that disagrees with the oracle's, and a dynamic body
                // would take collision responses the oracle never takes. The body is kept only so the
                // trigger collider raises overlap events.
                _body.bodyType     = RigidbodyType2D.Kinematic;
                _body.gravityScale = 0f;
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
            int    maxHp,
            // The placing weapon's blast presentation, carried exactly as Projectile carries its own.
            // Optional so existing editor/test callers keep working; the spawner always passes both.
            DamageType damageType = DamageType.PhysicalBullet,
            string visExpl   = null,
            // The placing weapon's `<snd sens>` — the arming beep. Optional like the two above.
            string soundSens = null)
        {
            _weaponId   = weaponId;
            _explRadius = explRadius;
            _damageType = damageType;
            _visExpl    = visExpl;
            _soundSens  = soundSens;

            int fuse = fuseFrames > 0 ? fuseFrames : DefaultFuseFrames;
            _explTimer = fuse * FuseScale / SimClock.FramesPerSecond;

            // AS3 `WThrow.as:155 explTime *= 0.3` applied to an `int` (Mine.as:20) TRUNCATES, so
            // `time='15'` gives 4 frames, not 4.5 — and 4 is what the beep's modulo counts down. The
            // seconds timer above is the same duration and is what fires the mine; this is the one the
            // cadence reads. See `_explTicks`.
            _explTicks = Mathf.FloorToInt(fuse * FuseScale);

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

        /// <summary>
        /// One step of the mine — <c>Mine.control()</c> then the Unit body. <see cref="FixedUpdate"/>
        /// is the production caller; public so a fixture can advance a mine by an exact delta without
        /// a running player loop, which an EditMode test does not have.
        /// </summary>
        public void Step(float dt)
        {
            if (_detonated) return;

            // AS3 `Unit.step()` (`Unit.as:1761`) is `forces()` → `control()` → `if(!this.fixed) run()`.
            // `control()` is the state machine below; `run()` is the body. They are SIBLING calls, so
            // `control()`'s arming branch returns early while the body still integrates that frame —
            // an arming mine falls. Folding the physics into the state machine (the obvious shape)
            // would freeze every mine in mid-air until it armed, which is the bug this split exists
            // to prevent.
            StepControl(dt);
            StepBody(dt);
        }

        /// <summary>
        /// AS3 <c>Mine.control()</c> (<c>Mine.as:290-358</c>), in the oracle's order.
        /// </summary>
        private void StepControl(float dt)
        {
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
                // AS3 Mine.control():340-345. The beep test runs BEFORE the decrement — the oracle
                // tests, then `--explTime` — so the tick that beeps is the one whose remaining count
                // is exactly 3, not 4. Order matters: swapping them shifts the beep a tick and, on a
                // 4-tick fuse, moves it to the very last frame before the blast.
                if (ShouldBeep(_explTicks))
                    PlayArmingBeep();

                // AS3 decrements here and tests afterwards, outside the branch — so the countdown is
                // what kills it, but the test itself runs every frame.
                _explTimer -= dt;
                if (_explTicks > 0) _explTicks--;
            }

            if (_explTimer <= 0f)
                _pendingDetonate = true;
        }

        // ── Body (AS3 `Unit.forces()` + `Unit.run()`) ─────────────────────────

        /// <summary>
        /// One step of the mine's own physics — AS3 <c>Unit.forces()</c> + <c>Unit.run()</c> for the
        /// non-flying branch, in <see cref="UnitController"/>'s order so the port keeps one unit model
        /// instead of growing a second one.
        ///
        /// <para><b>Gravity accumulates even while pinned.</b> The oracle gates only <c>run()</c>
        /// (<c>Unit.as:1809</c>), and <c>forces()</c> sits above that gate — so a fixed mine keeps
        /// building <c>dy</c> and simply never applies it. Same here: <see cref="_pinned"/> guards the
        /// position write alone.</para>
        ///
        /// <para><b>Not modelled, and recorded rather than invented:</b> <c>Unit.checkShelf</c>
        /// (<c>Unit.as:2713-2741</c>), so a mine that lands on a crate falls through it rather than
        /// resting on it. <see cref="UnitController"/> has that path; the mine does not yet, and the
        /// gap is named in the class note.</para>
        /// </summary>
        private void StepBody(float dt)
        {
            if (_bodyCollider == null || _body == null) EnsureBody();

            MineBodyStep step = MineBodyMath.Step(_tileQuery, _velocity, OriginPixels(), _pinned, dt);

            _velocity = step.Velocity;
            _grounded = step.Grounded;

            if (!step.Moved) return;

            // Written straight to the transform rather than through `Rigidbody2D.MovePosition`, and
            // this is the one place the mine's body differs from `UnitController.Move()`.
            //
            // The mine is its OWN integrator: it resolves its position from the tile grid and then
            // reads that position back on the next step through `OriginPixels()`. `MovePosition`
            // defers the write to the physics step, so the read-back is only correct if Unity happens
            // to run FixedUpdate callbacks before the simulation — the exact ordering fragility
            // `UnitController` documents when it snapshots the feet BEFORE its own `MovePosition`.
            // Writing the value we just resolved removes the dependency instead of relying on it.
            //
            // Nothing in Box2D is waiting on this move: the body is Kinematic with `gravityScale = 0`
            // and the mine takes no contact response (no wall damage, no knockback). The Rigidbody2D
            // is kept so the trigger collider generates overlap events against whatever hits it.
            // `TilePhysicsController` — the port's other self-integrating body — writes its transform
            // the same way.
            transform.position = new Vector3(
                step.OriginPixels.x * TileQueryConstants.PixelToUnit,
                step.OriginPixels.y * TileQueryConstants.PixelToUnit,
                transform.position.z);
        }

        /// <summary>
        /// AS3 <c>isLaz</c> — "am I standing on something", as of the last <see cref="StepBody"/>.
        ///
        /// <para><b>Cached from the step rather than re-asked.</b> The question is about the position
        /// the step started from, so a fresh query after the move would sample a different point and
        /// could legitimately disagree with the step that just ran. One step, one probe — see
        /// <see cref="MineBodyStep.Grounded"/>.</para>
        ///
        /// <para>Named to match <c>UnitController.IsGrounded</c>, which is the same flag on the same
        /// unit model. False before the first step and false with no room, which is also what
        /// <see cref="MineBodyMath.Step"/> reports for a null query.</para>
        /// </summary>
        public bool IsGrounded => _grounded;

        /// <summary>
        /// This mine's origin in <b>world pixels</b> — AS3's <c>X</c>/<c>Y</c>. The transform's
        /// position <i>is</i> that point: <c>Unit.setVisPos</c> does <c>vis.x = X; vis.y = Y</c>, and
        /// the visual child sits at local (0,0), so the sprite's pivot lands on the origin exactly as
        /// the oracle places it.
        /// </summary>
        private Vector2 OriginPixels()
        {
            Vector2 worldUnits = transform.position;
            return worldUnits * TileQueryConstants.UnitToPixel;
        }

        /// <summary>
        /// This mine's collision box in <b>world pixels</b> — AS3's <c>X1..X2 / Y1..Y2</c>, which
        /// <c>Unit.setPos</c> derives from <c>scX</c>/<c>scY</c> (<c>Unit.as:1875-1878</c>).
        ///
        /// <para><b>Derived from the AS3 constants, not from the collider.</b> The prefab's collider is
        /// authored to the same 30×20 box, but the collision question must not depend on an asset
        /// somebody can re-import wrong — that is the same reasoning <see cref="Awake"/> uses when it
        /// forces the trigger flag in code. The collider's job is the <i>hit</i> volume (a bullet finds
        /// the mine through it); this function's job is the oracle's physics box.</para>
        /// </summary>
        private Rect BodyRectWorldPixels()
        {
            return MineBodyMath.BoxRectPixels(OriginPixels());
        }

        // ── Placement (AS3 `WThrow.shoot()`'s throwTip == 1 tail) ─────────────

        /// <summary>
        /// Hand this mine the room's tile query. Called by <see cref="ProjectileSpawner"/> at spawn —
        /// the same handover, for the same reason, as <c>UnitController.SetTileQuery</c>.
        ///
        /// <para>Idempotent. A mine with no query keeps <c>null</c> and therefore never falls, which
        /// is the correct reduced answer for a mine with no room (a fixture, or a scene with no map)
        /// rather than a silent half-physics.</para>
        /// </summary>
        public void SetTileQuery(ITileQueryService tileQuery)
        {
            _tileQuery = tileQuery;
        }

        /// <summary>
        /// AS3 <c>Unit.collisionAll()</c> (<c>Unit.as:2541</c>) — does this mine's box overlap a solid
        /// tile? The placement retry's only question.
        /// </summary>
        /// <remarks>
        /// <para><c>collisionTile</c> returns 0 for an air tile and for a stair tile when the unit is
        /// <c>transT</c>. <c>Mine.as</c> sets <c>transT = true</c> (<c>:189</c>), so the transparent
        /// flag is passed through rather than defaulted — a mine placed inside a staircase is not
        /// "inside geometry" to the oracle.</para>
        ///
        /// <para>No tile query means no room, and the honest answer to "is this inside geometry" with
        /// no geometry to ask is <c>false</c>: the mine is left where it is rather than pinned in a
        /// room that does not exist.</para>
        /// </remarks>
        public bool CollidesWithTiles()
        {
            if (_tileQuery == null) return false;

            var options = new TileQueryOptions(isTransparent: true);
            return _tileQuery.CheckCollision(BodyRectWorldPixels(), options);
        }

        /// <summary>
        /// AS3 <c>Unit.setPos()</c> (<c>Unit.as:1872-1880</c>) — teleport the mine, box and all. Used
        /// only by the placement retry.
        /// </summary>
        public void PlaceAt(Vector2 worldPosition)
        {
            transform.position = new Vector3(worldPosition.x, worldPosition.y, 0f);
            _velocity = Vector2.zero;
        }

        /// <summary>
        /// AS3 <c>_loc5_.fixed = true</c> (<c>WThrow.as:171</c>) — the mine could not be placed
        /// anywhere legal, so it becomes a static obstacle that never falls.
        /// </summary>
        public void PinInPlace()
        {
            _pinned = true;
            _velocity = Vector2.zero;
        }

        /// <summary>AS3 <c>Unit.fixed</c>. True only for a mine that spawned inside geometry.</summary>
        public bool IsPinned => _pinned;

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

        // ── Visual ────────────────────────────────────────────────────────────

        /// <summary>
        /// Applies the placing weapon's own art — <c>WeaponDefinition.weaponVisual</c>, i.e.
        /// <c>vis&lt;weaponId&gt;</c>, which is literally what <c>Mine.as:98</c> resolves
        /// (<c>Res.getVis("vis" + id, vismine)</c>).
        /// </summary>
        public void ApplyVisual(WeaponVisualDefinition visual)
        {
            EnsureVisualRenderer();

            _currentVisual    = visual;
            _visualFrameTimer = 0f;
            _visualFrameIndex = 0;

            if (_visualRenderer == null) return;

            if (visual == null || visual.frames == null || visual.frames.Length == 0)
            {
                // No art imported for this mine yet — draw nothing rather than a white box.
                _visualRenderer.enabled = false;
                return;
            }

            _visualRenderer.enabled = true;
            _visualRenderer.sprite  = visual.frames[0];
        }

        /// <summary>
        /// Finds the sprite renderer, creating the child if the prefab has none — see
        /// <see cref="ThrownObject.EnsureVisualRenderer"/> for why a child is created rather than a
        /// renderer added to this object: this transform carries the trigger collider that
        /// <see cref="Scan"/> and <see cref="Detonate"/> read, and a child is also where AS3 keeps it.
        /// </summary>
        private void EnsureVisualRenderer()
        {
            if (_visualRenderer != null)
            {
                if (_visualTransform == null) _visualTransform = _visualRenderer.transform;
                return;
            }

            _visualRenderer = GetComponentInChildren<SpriteRenderer>();
            if (_visualRenderer != null)
            {
                _visualTransform = _visualRenderer.transform;
                return;
            }

            var visualObject = new GameObject(VisualChildName);
            _visualTransform = visualObject.transform;
            _visualTransform.SetParent(transform, false);

            _visualRenderer         = visualObject.AddComponent<SpriteRenderer>();
            _visualRenderer.enabled = false;

            // The project's "Default" sorting layer sits BELOW the map tiles, so a renderer left on it
            // draws behind the floor. Fall back to the projectile's own layer when the serialized field
            // is empty — which is what a prefab authored before this field existed deserialises to.
            _visualRenderer.sortingLayerName = string.IsNullOrEmpty(_visualSortingLayer)
                ? MapSortingLayers.Foreground
                : _visualSortingLayer;
        }

        private void Update()
        {
            UpdateMineVisual();
        }

        /// <summary>
        /// The mine's three view states, tested in <c>control()</c>'s order.
        ///
        /// <para><b>The arming test is first and returns</b>, because <c>Mine.as:296-304</c> does: a
        /// mine that has been radio-activated while still arming keeps the un-armed frame until the
        /// arming finishes, since the branch that flips it to frame 2 has not run. Testing
        /// <c>aiState</c> first would blink a mine that cannot yet fire — the same inversion the
        /// class note warns about for the two state fields.</para>
        /// </summary>
        private void UpdateMineVisual()
        {
            if (_currentVisual == null || _visualRenderer == null) return;

            var frames = _currentVisual.frames;
            if (frames == null || frames.Length == 0) return;

            if (_armingTimer > 0f)
            {
                ShowStaticFrame(0, frames);
                return;
            }

            if (_aiState == 1)
            {
                ShowStaticFrame(ArmedFrameIndex, frames);
                return;
            }

            // Activated — `Mine.as:282 vis.play()`, the countdown blink.
            AnimateFromFrame(ArmedFrameIndex, frames);
        }

        private void ShowStaticFrame(int index, Sprite[] frames)
        {
            int i = Mathf.Clamp(index, 0, frames.Length - 1);

            _visualFrameTimer = 0f;
            _visualFrameIndex = i;
            _visualRenderer.sprite = frames[i];
        }

        /// <summary>
        /// Loops the timeline from <paramref name="startIndex"/>, the way Flash's <c>play()</c> does.
        /// A single-frame symbol has nothing to loop, so it stays on that frame.
        /// </summary>
        private void AnimateFromFrame(int startIndex, Sprite[] frames)
        {
            int start = Mathf.Clamp(startIndex, 0, frames.Length - 1);
            int span  = frames.Length - start;

            // Entering the animated state (or a symbol with one frame in range): park on the start
            // frame rather than advancing, so the first frame of the blink is shown for a full period.
            if (span <= 1 || _visualFrameIndex < start)
            {
                ShowStaticFrame(start, frames);
                return;
            }

            _visualFrameTimer += Time.deltaTime;

            float frameDuration = 1f / SimClock.FramesPerSecond;
            if (_visualFrameTimer < frameDuration) return;

            int advance = Mathf.Min(Mathf.FloorToInt(_visualFrameTimer / frameDuration), span);
            _visualFrameTimer -= advance * frameDuration;
            _visualFrameIndex  = start + ((_visualFrameIndex - start + advance) % span);

            _visualRenderer.sprite = frames[_visualFrameIndex];
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

                // The blast's visuals and sound, as a SIBLING of the damage loop — AS3 runs them last
                // within the same routine, from the Bullet `Unit.explosion()` constructs. Kept out of
                // the loop so one blast emits once, not once per victim.
                EmitExplosionVisuals(centre);
            }

            ReturnToPool();
        }

        /// <summary>
        /// The arming beep — AS3 <c>Mine.control()</c>'s
        /// <c>if(this.sndSens != "" &amp;&amp; this.explTime % 5 == 3) Snd.ps(this.sndSens, X, Y)</c>
        /// (<c>Mine.as:340-343</c>).
        ///
        /// <para><b>The <c>!= ""</c> guard is the oracle's and is load-bearing.</b> <c>snd sens</c>
        /// lives only on the eight mine rows, so every non-mine weapon arrives here with an empty id —
        /// and a substitute beep would make a mine out of a grenade. Silent is correct.</para>
        /// </summary>
        private void PlayArmingBeep()
        {
            if (string.IsNullOrEmpty(_soundSens)) return;

            _soundService?.Play(_soundSens, transform.position);
        }

        /// <summary>
        /// Emits the blast's particles and plays its sound — the port's mirror of AS3
        /// <c>Bullet.explVis()</c> (<c>weapon/Bullet.as:878-1000</c>), reached by a mine because
        /// <c>Unit.explosion()</c> (<c>Unit.as:3328</c>) routes its blast through a throwaway
        /// <c>Bullet</c> rather than drawing it itself.
        ///
        /// <para><b>The inputs are the placing weapon's, not the mine's.</b> <c>Unit.explosion()</c> sets
        /// <c>_loc8_.weapId = this.id</c>, so the bullet resolves the same weapon the mine came from and
        /// reads <i>its</i> <c>visexpl</c>. The port passes that weapon's <c>visExpl</c> and
        /// <c>damageType</c> in through <see cref="Initialize"/>.</para>
        ///
        /// <para><b>Same seam as <see cref="Projectile"/> and <see cref="ThrownObject"/>.</b> The table is
        /// Unity-free (<see cref="PFE.Systems.Particles.ExplosionVisualRules.Plan"/>); this method only
        /// converts the position and forwards the emits. <c>inWater</c> is derived at the impact point
        /// rather than stored. The table has no default arm, so a mine whose weapon has neither a
        /// <c>visexpl</c> nor a table arm emits nothing — AS3's own behaviour, not a fallback.</para>
        /// </summary>
        private void EmitExplosionVisuals(Vector3 centre)
        {
            if (_particles == null) return;

            // No room pushed ⇒ no origin and no height ⇒ no correct position. Refusing is the point of
            // the adapter's contract: emitting anyway would place the blast somewhere plausible and wrong.
            if (!_particles.TryToAs3Local(centre, out Vector2 as3Local)) return;

            int water = _particleTileWater != null
                ? _particleTileWater.WaterAt(as3Local.x, as3Local.y)
                : 0;

            if (!PFE.Systems.Particles.ExplosionVisualRules.Plan(
                    _visExpl, _damageType, water > 0,
                    // Always the initial pulse: a mine is never a sustained blast. `Mine.as:258` calls
                    // `explosion(damage1, tipDamage, explRadius, 0, …)` — the fourth argument is
                    // `explKol`, hardcoded 0 — so a mine always runs exactly one pulse. The data agrees:
                    // of the two mines that carry an `explkol` at all, `impmine` has 1 and `hmine` has
                    // none. If a mine ever gains a train this has to become a real pulse index.
                    isInitialPulse: true,
                    PresentationRng(), _explosionEmits,
                    out string soundId))
            {
                return;
            }

            foreach (PFE.Systems.Particles.ParticleEmit emit in _explosionEmits)
            {
                _particles.EmitAt(emit.Id,
                    new Vector2(as3Local.x + emit.OffsetX, as3Local.y + emit.OffsetY),
                    emit.Spec);
            }

            if (soundId != null) _soundService?.Play(soundId, centre);
        }

        /// <summary>
        /// The unseeded presentation stream, resolved once — the only jitter these visuals need is the
        /// acid arm's <c>kol</c>. Null when nothing injected an <see cref="PFE.Core.Rng.IRngService"/>
        /// (an offline fixture built with <c>new</c>), which the rules treat as "use the oracle's
        /// minimum" rather than an error.
        /// </summary>
        private PFE.Core.Rng.IRngService PresentationRng()
        {
            if (_presentationRng == null && _rng != null)
                _presentationRng = _rng.GetStream(PFE.Core.Rng.RngStream.Presentation);
            return _presentationRng;
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
            _explTicks        = 0;
            _senseTimer       = 0f;
            _explRadius       = 0f;
            _sens             = 0f;
            _hp               = 0f;
            _maxHp            = 0f;
            _weaponId         = null;
            _ownerFaction     = FactionType.Neutral;

            // A recycled mine must not inherit the previous placement's blast, or a sparkle mine would
            // make the next plain one emit sparkle visuals. Same rule as Projectile.ResetForPool.
            _visExpl          = null;
            _damageType       = DamageType.PhysicalBullet;

            // Same reason for the beep: a recycled mine that kept the last weapon's `<snd sens>` would
            // chirp in the wrong voice, and one that kept a stale countdown would chirp immediately.
            _soundSens        = null;

            // View state. `_currentVisual` is kept — the next placement re-applies its own — but the
            // animation cursor must not carry over, or a reused instance would start mid-blink.
            _visualFrameTimer = 0f;
            _visualFrameIndex = 0;

            // Body state. A recycled mine must not inherit the last placement's fall: a mine that was
            // mid-drop would resume it, and one that had been pinned would stay pinned in the new
            // room. `_tileQuery` is cleared too — it is room-scoped, and the spawner hands over a
            // fresh one on every placement, so a stale room must never survive the pool.
            _velocity  = Vector2.zero;
            _pinned    = false;
            _grounded  = false;
            _tileQuery = null;
        }

        // ── IDamageable ───────────────────────────────────────────────────────
        // The oracle's mine is a Unit with hp = maxhp = <char maxhp> (10 on all eight rows), so it can
        // be shot and destroyed before it fires. Everything else here is the AS3 declaration default
        // for a mine: no armour, no skin, no evasion. It is NOT fixed in place by default — see the
        // body fields; `fixed` is set only by the placement retry.

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
