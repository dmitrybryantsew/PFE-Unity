using System;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Campaign;
using PFE.Systems.Combat;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Security;
using PFE.Systems.Weapons;
using UnityEngine;

namespace PFE.Entities.Enemies.Bosses
{
    /// <summary>
    /// The raider leader — a port of <c>UnitBossRaider</c> (<c>UnitBossRaider.as</c>, 583 lines).
    ///
    /// <para><b>Which boss this is, and where it is fought.</b> <c>bossraider</c> (Главарь рейдеров,
    /// 2000 HP, <c>AllData.as:193-202</c>) is the raider leader, and it is placed in <b>two bodies from
    /// one unit row</b>: <c>UnitBossRaider.as:54-61</c> builds <c>visualRaiderBoss2</c> when
    /// <c>tr == 2</c> and <c>visualRaiderBoss</c> for every other value, and <c>tr</c> defaults to
    /// <b>1</b> (<c>:12</c>). The land room — <c>rooms_raiders/room_8_1</c> (<c>uid="buch"</c>) — authors
    /// <b>no</b> <c>tr</c>, so the land boss is the <b>carbine</b> body at <c>aiDist = 2000</c>. The
    /// three <c>tr='2'</c> rooms (<c>rooms_nio/bossraider</c>, <c>rooms_prob/bossraider</c>,
    /// <c>rooms_prob/bossraider1</c>) are the flamer at <c>aiDist = 500</c>;
    /// <c>rooms_prob/bossraider2</c> also authors none and is a second carbine. The row ids look
    /// inverted because <c>bossraider</c> must stay the id rooms place, so the carbine body took
    /// <c>bossraider1</c> — see <see cref="BossRaiderVariants"/>, which owns that mapping.</para>
    ///
    /// <para><b>Provenance note.</b> <c>random_plant</c> also authors a <c>&lt;prob1 id='bossraider'
    /// imp='1' close='1'&gt;</c> room (<c>GameData.as:133-135</c>) whose placement carries <c>tr='2'</c>,
    /// but that node is <b>dead data in the oracle as well</b> (<c>Land.newRandomProb</c> iterates
    /// <c>xmlland.prob</c>, and E4X does not match <c>prob1</c>), so it is not the fight the player
    /// meets and is not what this brain is for.</para>
    ///
    /// <para><b>Why the whole tick is replaced.</b> Not one boss extends a family class —
    /// <c>UnitBossRaider extends UnitPon</c> (<c>:9</c>), a <i>presentation</i> base — and its
    /// <c>control()</c> (<c>:287-507</c>) replaces the ladder wholesale with a timer rotation. So this
    /// overrides <see cref="EnemyBrain.SimTick"/> rather than the per-state ticks, and the port's
    /// <see cref="EnemyAIState"/> is used only to carry the AS3 <c>aiState</c> integer, never as a
    /// phase.</para>
    ///
    /// <para><b>What was here before, and why it read as "no boss logic".</b> The previous body invented
    /// its rotation (<c>Random.Range(150, 250)</c> against the oracle's <c>150 + floor(random*100)</c>),
    /// used hard-coded ranges (600/250/70 px) instead of the <c>tr</c>-selected <c>aiDist</c>
    /// (2000/500), never read <c>tr</c> from the placement at all, fired <c>PendingDamage.Direct</c>
    /// instead of the held weapon — <b>so the boss had no gun, no projectile and no flare</b> — dealt
    /// 25 damage from a quake that in the oracle only shakes the camera, and returned
    /// <c>"stay"</c> from <see cref="ResolveAnimState"/> for every state, which is the "no animation"
    /// half: <c>run</c>, <c>trot</c>, <c>jump</c> and <c>bac</c> were unreachable.</para>
    ///
    /// <para><b>Deliberately not ported, with the reason at the call site:</b> <c>loc.earthQuake</c>'s
    /// camera shake (<see cref="QuakeRequested"/> is the seam, and the port has no shake system),
    /// <c>emit()</c>'s vortex child unit (<see cref="OnVortexEmitRequested"/> — the room API can spawn a
    /// <i>family</i>, not an exact unit id), the <c>porog</c>/<c>destroy</c> writes (the port's step-up
    /// is a constant by an existing decision — <c>UnitWallMotion</c>, <c>UnitDropThroughMath</c>), the
    /// pet-targeting 40 % roll (no companion exists in the port), and <c>setLevel</c>'s
    /// difficulty HP/damage multiply (no unit-level <c>setLevel</c> exists anywhere in the port yet).</para>
    /// </summary>
    public class BossRaiderBrain : BossBrain
    {
        // ── The oracle's numbers, all in 30 Hz ticks ───────────────────────────────────────────────
        //
        // The rotation literals, the animation branch order, the two contact scales and the
        // reinforcement gate live in `BossRaiderRules` — not here. They are the four places this brain
        // used to hold an invented value that survived a structural review, and a rule that lives on a
        // MonoBehaviour cannot be executed by a fixture (AddComponent reaches an ECall). The constants
        // below stay local because nothing but this tick reads them.

        /// <summary><c>aiTCh = 80</c> in the constructor (<c>:70</c>) — see <see cref="BossRaiderRules.InitialAiTCh"/>.</summary>
        const int InitialAiTCh = BossRaiderRules.InitialAiTCh;

        /// <summary><c>aiAttackOch = 12</c> (<c>:34</c>) — the burst window, in ticks.</summary>
        const int AttackOch = 12;

        /// <summary><c>if(aiTCh % 10 == 1)</c> — the <c>setCel</c> poll cadence (<c>:340</c>).</summary>
        const int CelCadence = 10;

        /// <summary><c>if(aiTCh % 15 == 1)</c> — the 50 % re-aim cadence (<c>:399-412</c>).</summary>
        const int RerollCadence = 15;
        const float RerollChance = 0.5f;

        /// <summary><c>aiDist = 2000</c> for <c>tr == 1</c> (carbine, <c>:74</c>).</summary>
        const float AiDistCarbine = 2000f;

        /// <summary><c>aiDist = 500</c> for <c>tr == 2</c> (flamer, <c>:79</c>).</summary>
        const float AiDistFlamer = 500f;

        /// <summary><c>celDX*celDX + celDY*celDY &lt; 200*200</c> — the "close enough to stand and shoot" test (<c>:501</c>).</summary>
        const float StandRangePixels = 200f;

        /// <summary><c>&lt; 100*100</c> — the point-blank melee probe in states 3 and 5 (<c>:530</c>, <c>:556</c>).</summary>
        const float MeleeRangePixels = 100f;

        /// <summary><c>runSpeed = maxSpeed * 6</c> (<c>:66</c>).</summary>
        const float RunSpeedMultiplier = 6f;

        /// <summary><c>aiTCh == 5</c> — the quake / vortex beat, once per 30-tick window (<c>:537</c>, <c>:544</c>).</summary>
        const int BeatTick = 5;

        /// <summary><c>isrnd(0.1)</c> — the point-blank body hit in states 3 and 5 (<c>:530</c>, <c>:556</c>).</summary>
        const float MeleeProbeChance = 0.1f;

        /// <summary><c>isrnd(0.02)</c> — the stray weapon shot in state 5 (<c>:548</c>).</summary>
        const float VortexStrayShotChance = 0.02f;

        /// <summary><c>Y - begY &gt;= 40</c> — climb back toward the spawn height (<c>:464-467</c>), in pixels.</summary>
        const float SpawnHeightReturnPixels = 40f;

        /// <summary><c>celDY &lt; -100</c> — the flamer wants the high ground (<c>:468-471</c>), in pixels.</summary>
        const float FlamerHighGroundPixels = 100f;

        /// <summary><c>throu = celDY &gt; 160</c> — drop through a one-way platform to a target well below (<c>:369</c>).</summary>
        const float DropThroughPixels = 160f;

        /// <summary><c>aiVNapr</c>'s dead band, <c>celDY &gt; 40 / &lt; -40</c> (<c>:356-367</c>).</summary>
        const float VerticalDeadBandPixels = 40f;

        /// <summary><c>Math.abs(celDX) &gt; 100</c> — the re-aim flip threshold (<c>:403-410</c>).</summary>
        const float RerollFlipPixels = 100f;

        // ── State ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>AS3 <c>tr</c> (<c>:12</c>) — 1 = carbine, 2 = flamer. Read from the placement.</summary>
        [SerializeField] protected int _variant = 1;

        /// <summary>
        /// AS3 <c>aiState</c> (<c>Unit.as</c>, reused here as the rotation ladder). Starts at <b>0</b> and
        /// wraps 5 → 1 — <b>never back to 0</b> (<c>:326-330</c>). State 0 is only ever the spawn state.
        /// </summary>
        [SerializeField] protected int _bossState;

        protected int _aiTCh = InitialAiTCh;
        protected int _aiJump;
        protected int _aiAttackT;
        protected int _kolEmit = 8;

        /// <summary>AS3 <c>called</c> (<c>:22</c>) — how many quarter-HP reinforcements have been called.</summary>
        protected int _called;

        /// <summary>AS3 <c>celX</c>/<c>celY</c>, in pixels, refreshed on the <see cref="CelCadence"/> poll.</summary>
        float _celX;
        float _celY;
        float _celDX;
        float _celDY;
        int _aiVNapr;

        float _spawnWorldY;
        bool _spawnAnchored;

        /// <summary><c>aiDist</c>, selected by <c>tr</c> in the constructor (<c>:71-80</c>).</summary>
        public float AiDistance => _variant == 2 ? AiDistFlamer : AiDistCarbine;

        public int BossState => _bossState;
        public int Variant { get => _variant; set => _variant = value; }
        public int AiTicks => _aiTCh;

        /// <summary>How many quarter-HP reinforcement calls have been made (<c>called</c>, <c>:22</c>).</summary>
        public int ReinforcementsCalled => _called;

        /// <summary>
        /// Raised with the magnitude when the boss slams the floor — AS3 <c>loc.earthQuake(40)</c>
        /// (<c>:565</c>). <b>Nothing in the port shakes the camera yet</b>, so the event is the seam and
        /// the default subscriber list is empty; the behaviour it replaces dealt 25 damage, which the
        /// oracle never does.
        /// </summary>
        public event Action<float> QuakeRequested;

        BossRaiderController RaiderController => _controller as BossRaiderController;

        /// <summary>
        /// The room the boss is standing in — `loc` in the oracle, and the receiver of
        /// `loc.enemySpawn(true, true)`. <see cref="BossBrain"/> has no such property; the
        /// controller owns it (<c>UnitController.Room</c>), and <c>BossAlicornBrain</c> declares the same
        /// one-liner for the same reason.
        /// </summary>
        public RoomInstance Room => _controller != null ? _controller.Room : null;

        protected override bool StopsToAttack => _bossState == 2 || _bossState == 3;

        protected override void Awake()
        {
            base.Awake();
            ApplyTuning();
        }

        public override void OnDefinitionAssigned()
        {
            base.OnDefinitionAssigned();
            ApplyTuning();
        }

        /// <summary>
        /// AS3's constructor speed block (<c>:64-68</c>): <c>walkSpeed = maxSpeed; plavSpeed = maxSpeed;
        /// runSpeed = maxSpeed * 6;</c>. Note this is <b>not</b> the family's <c>runSpeed</c> — a raider
        /// runs at 2× and the boss at 6×, which is why the boss crosses a 2000 px room in seconds.
        /// </summary>
        protected virtual void ApplyTuning()
        {
            if (_controller == null || _controller.Definition == null) return;

            UnitDefinition def = _controller.Definition;
            float walk = def.moveSpeed > 0 ? def.moveSpeed : 3f;

            _patrolSpeed = walk;
            _chaseSpeed = walk * RunSpeedMultiplier;
            _alertSpeed = _chaseSpeed;
        }

        /// <summary>
        /// The placement half of the constructor — <c>if(param3.@tr.length()) this.tr = param3.@tr;</c>
        /// (<c>:50-53</c>), then the weapon that <c>tr</c> selects (<c>:71-80</c>) and the body it
        /// selects (<c>:54-61</c>).
        ///
        /// <para><b>Both variants are reachable, and the land one is the carbine.</b> Of the five
        /// <c>bossraider</c> placements in the imported rooms, three author <c>tr='2'</c> —
        /// <c>rooms_nio/bossraider</c>, <c>rooms_prob/bossraider</c>, <c>rooms_prob/bossraider1</c> — and
        /// two author nothing: <c>rooms_prob/bossraider2</c> and <c>rooms_raiders/room_8_1</c>. An absent
        /// <c>tr</c> falls back to the field's initialiser (<c>:12</c>, <c>public var tr:int = 1</c>), so
        /// the land raider-boss room is the <b>carbine</b> body at <c>aiDist = 2000</c>, and only the
        /// three <c>tr='2'</c> rooms are the flamer at <c>aiDist = 500</c>. Without this read the port
        /// ran every boss as <c>tr == 1</c> at carbine range with no weapon equipped at all.</para>
        ///
        /// <para><b>The body swap has to be here and not in the controller.</b> <c>tr</c> arrives with
        /// the placement, which is applied after <c>Initialize</c>; the controller cannot know which
        /// clip to draw before this method has run. <see cref="BossRaiderController.ApplyVariantVisual"/>
        /// is idempotent, so a re-applied placement costs one string compare.</para>
        /// </summary>
        public override void OnPlacementApplied(UnitInstance placement)
        {
            base.OnPlacementApplied(placement);

            string tr = placement?.GetAttribute("tr", null);
            if (!string.IsNullOrEmpty(tr) && int.TryParse(tr, out int parsed) && parsed > 0)
            {
                _variant = parsed;
            }

            AnchorSpawnHeight();

            // `vis = new visualRaiderBoss2()` / `new visualRaiderBoss()` — the half of the constructor
            // that decides what the boss LOOKS like. Order against EquipBossWeapon is irrelevant (both
            // only read `_variant`), but both must run after the `tr` read above.
            RaiderController?.ApplyVariantVisual();
            RaiderController?.EquipBossWeapon();
        }

        /// <summary>
        /// Record the spawn anchor for <c>Y - begY &gt;= 40</c> (<c>:464-467</c>). AS3 stores
        /// <c>begX</c>/<c>begY</c> in the constructor and <c>setNull</c> restores them; the port has no
        /// spawn-anchor field, so the first tick that has a position records it.
        /// </summary>
        void AnchorSpawnHeight()
        {
            if (_spawnAnchored) return;
            _spawnWorldY = transform.position.y;
            _spawnAnchored = true;
        }

        public override void Initialize(ITileQueryService tileQuery, SimLoop simLoop)
        {
            base.Initialize(tileQuery, simLoop);
            AnchorSpawnHeight();
        }

        /// <summary>
        /// AS3 <c>control()</c> (<c>:287-507</c>), plus the two things the base tick did around it —
        /// the quarter-HP latch (<c>BossBrain</c>) and the arena clamp, which is a no-op here because no
        /// room bounds the raider (only <c>UnitBossEncl</c> has a hard box).
        /// </summary>
        public override void SimTick(int tickIndex)
        {
            if (_controller == null) return;

            // `UnitBossRaider.damage()`'s quarter-HP reinforcement is latched here rather than in
            // `damage()` because the port's damage path reports through `DamageSystem`, not through a
            // virtual the brain owns. Same observable: one call per quarter band, monotone.
            //
            // <b>Before the corpse branch, deliberately.</b> `damage()` runs its reinforcement block
            // <i>after</i> `super.damage()` (:232-241), and `super.damage()` is what calls `die()` — so
            // the killing blow still computes `_loc6_ = 4` on the body it just killed and calls the
            // fourth wave from the grave. Checking after the `IsAlive` return would cap the boss at
            // three waves. See `CheckQuarterHpThresholds` for the other half of that gate.
            CheckQuarterHpThresholds();

            // AS3's first line, `if(sost == 3) return;` — but a corpse still draws, which is the port's
            // `EnemyBrain` convention (the death row is `die`, and this unit authors no `death`/`fall`).
            if (!_controller.IsAlive)
            {
                UpdateAnimation();
                return;
            }

            // `if(World.w.enemyAct <= 0)` — the boss stops deciding, but keeps drawing and keeps its
            // weapon ticking (the oracle's `control()` returns mid-frame; physics and `animate()` run on).
            if (EnemyActivity.IsFrozen)
            {
                UpdateAnimation();
                TickWeapons();
                return;
            }

            UpdatePerception(tickIndex);
            TickControl(tickIndex);
            UpdateAnimation();
            TickWeapons();
            ClampToArenaBounds();
        }

        /// <summary>
        /// The quarter-HP latch, with the base class's <c>!IsAlive</c> term removed.
        ///
        /// <para><b>Why the base guard is wrong for this boss.</b> <c>BossBrain.CheckQuarterHpThresholds</c>
        /// returns on a corpse, which is the safe default for a phase table — a dead boss should stop
        /// changing phase. But the raider's quarter-HP event is not a phase: it is
        /// <c>damage():229-243</c>, which computes its quarter <b>after</b> <c>super.damage()</c> has
        /// already run <c>die()</c>. A killing blow therefore reads <c>_loc6_ = floor((maxhp-0)/maxhp*4)
        /// = 4</c> and, if <c>called</c> is still 3, calls the last wave with its dying breath. Keeping
        /// the base guard would cap the boss at three waves and lose the only reinforcement the player
        /// sees on the kill.</para>
        ///
        /// <para><b>Nothing else changes.</b> The loop is the base loop: monotone, capped at four, one
        /// event per newly crossed quarter, so a corpse cannot re-fire a quarter it has already paid
        /// for and a boss parked at 1 HP does not spawn a wave per tick.</para>
        /// </summary>
        protected override void CheckQuarterHpThresholds()
        {
            if (_controller == null || _maxHp <= 0f) return;

            int quartersPassed = BossRaiderRules.QuarterFor(_maxHp, _controller.CurrentHealth);

            while (_quarterHpEventsTriggered < quartersPassed && _quarterHpEventsTriggered < 4)
            {
                _quarterHpEventsTriggered++;
                OnQuarterHpPassed(_quarterHpEventsTriggered);
                RaiseQuarterHpThreshold(_quarterHpEventsTriggered);
            }
        }

        /// <summary>
        /// <c>control()</c>'s body, in the oracle's order: stun latch, timers, rotation, the <c>setCel</c>
        /// poll, then the state ladder, then <c>attack()</c>.
        /// </summary>
        protected virtual void TickControl(int tickIndex)
        {
            // `if(stun) { aiState = 0; aiTCh = 3; walk = 0; }` (<c>:294-299</c>).
            if (_blackboard.ShockTimerTicks > 0)
            {
                _bossState = 0;
                _aiTCh = 3;
            }

            // `if(this.aiJump > 0) --this.aiJump;` (<c>:316-319</c>). `aiLaz` is not modelled: it is the
            // levitation latch, and this unit is not a levitator.
            if (_aiJump > 0) _aiJump--;

            // `if(aiTCh > 0) --aiTCh; else { ++aiState; if(aiState > 5) aiState = 1; ... }` (<c>:320-339</c>).
            if (_aiTCh > 0)
            {
                _aiTCh--;
            }
            else
            {
                AdvanceRotation();
            }

            // `if(aiTCh % 10 == 1) { if(pet && ...) setCel(pet); else setCel(gg); }` (<c>:340-350</c>).
            // The pet half is not ported — the port has no companion unit.
            if (_aiTCh % CelCadence == 1)
            {
                RefreshCel();
            }

            UpdateCelDeltas();

            // `porog = 10; throu = celDY > 160; if(celDY > 70) porog = 0;` (<c>:368-373</c>).
            // `porog` is not modelled: the port's step-up is a constant by an existing decision
            // (`UnitWallMotion` / `UnitDropThroughMath` document why `porog = 0` was left out).
            // `throu` is, and it is the one that is observable.
            SetDropThroughPlatforms(_celDY < -DropThroughPixels);

            // `destroy = 50` (<c>:398</c>) is a property-destruction stat with no port counterpart.

            if (_bossState == 0)
            {
                // `if(dx > 0.5) storona = 1; if(dx < -0.5) storona = -1; walk = 0;` (<c>:384-395</c>).
                float dx = CurrentVelocityX;
                if (dx > 0.5f) ApplyFacing(1);
                else if (dx < -0.5f) ApplyFacing(-1);
                StopMovement();
            }
            else if (_bossState == 1 ||
                     ((_bossState == 2 || _bossState == 3) && Mathf.Abs(_celDX) > AiDistance))
            {
                TickApproach(tickIndex);
            }
            else if (_bossState == 3 || _bossState == 2)
            {
                // `else if(aiState == 3 || aiState == 2) { walk = 0; aiNapr = storona = celX > X ? 1 : -1;
                //  if(celDX*celDX + celDY*celDY < 200*200) aiState = 1; }` (<c>:497-505</c>).
                StopMovement();
                ApplyFacing(_celDX > 0f ? 1 : -1);

                if (_celDX * _celDX + _celDY * _celDY < StandRangePixels * StandRangePixels)
                {
                    _bossState = 1;
                }
            }

            Attack();
        }

        /// <summary><c>++aiState; if(aiState &gt; 5) aiState = 1;</c> then the per-state timer (<c>:326-338</c>).</summary>
        /// <remarks>
        /// <b>The draw happens only in the long arm.</b> <c>aiTCh = 30</c> is a literal, so states
        /// 2/4/5 consume <b>no</b> random number; drawing first and then discarding would shift every
        /// later roll (the burst's <c>Math.round((random*0.4+0.8) * aiAttackOch)</c> at <c>:522</c>, and
        /// every <c>isrnd</c> in <c>attack()</c>). <see cref="BossRaiderRules.TimerFor"/> therefore
        /// takes the roll as an argument instead of drawing one.
        /// </remarks>
        protected virtual void AdvanceRotation()
        {
            _bossState = BossRaiderRules.NextState(_bossState);

            _aiTCh = BossRaiderRules.UsesShortTimer(_bossState)
                ? BossRaiderRules.TimerFor(_bossState, 0)
                : BossRaiderRules.TimerFor(
                    _bossState,
                    UnityEngine.Random.Range(0, BossRaiderRules.LongAiTChRollRange));
        }

        /// <summary>
        /// The run arm (<c>:396-496</c>) — approach at <c>runSpeed</c> in state 1, at <c>walkSpeed</c> in
        /// 2/3 (where it is only reached while still outside <c>aiDist</c>).
        /// </summary>
        protected virtual void TickApproach(int tickIndex)
        {
            // `if(aiTCh % 15 == 1 && aiState != 6) if(isrnd(0.5)) { if(celDX > 100) aiNapr = storona = 1;
            //  if(celDX < -100) aiNapr = storona = -1; }` (<c>:399-412</c>).
            if (_aiTCh % RerollCadence == 1 && UnityEngine.Random.value < RerollChance)
            {
                if (_celDX > RerollFlipPixels) ApplyFacing(1);
                else if (_celDX < -RerollFlipPixels) ApplyFacing(-1);
            }

            float maxSpeed = _bossState == 1 ? _chaseSpeed : _patrolSpeed;
            int facing = _controller.FacingDirection >= 0 ? 1 : -1;

            // AS3 accumulates `dx += accel` toward `maxSpeed`; the port's seam is a desired speed and the
            // motor owns the ramp, so the cap is what is expressed here.
            MoveHorizontal(facing * maxSpeed);

            // `if(turnX != 0) { ... aiNapr = storona = turnX; ... }` (<c>:472-489</c>). `turnX` is written by
            // the oracle's physics pass on a wall hit (`Unit.as:2070`, `:2080`); the port's motor exposes
            // the same fact as `WallLeft`/`WallRight`, so the reversal is read from there.
            if (_motor != null && _controller.IsGrounded)
            {
                bool wallAhead = facing < 0 ? _motor.State.WallLeft : _motor.State.WallRight;
                if (wallAhead)
                {
                    ApplyFacing(-facing);
                    MoveHorizontal(-facing * maxSpeed);
                }
            }

            // `if(_loc2_ > 0 && this.aiJump <= 0) { storona = aiNapr; this.jump(_loc2_); }` (<c>:490-495</c>).
            if (_controller.IsGrounded && _aiJump <= 0)
            {
                float jumpScale = ResolveJumpScale();
                if (jumpScale > 0f)
                {
                    Jump(jumpScale);
                }
            }
        }

        /// <summary>
        /// The two jump triggers that can be expressed against the port's state, and the one that cannot.
        ///
        /// <para><b>Ported:</b> <c>Y - begY &gt;= 40</c> — the boss has dropped 40 px below where it
        /// spawned and hops back up (<c>:464-467</c>) — and <c>tr == 2 &amp;&amp; celDY &lt; -100</c> — the
        /// flamer wants to be above its target (<c>:468-471</c>). AS3's Y grows downward, so both signs
        /// are mirrored once, here.</para>
        ///
        /// <para><b>Not ported:</b> <c>stay &amp;&amp; isrnd(0.5) &amp;&amp; aiVNapr &lt;= 0 &amp;&amp;
        /// (shX1 &gt; 0.5 &amp;&amp; aiNapr &lt; 0 || shX2 &gt; 0.5 &amp;&amp; aiNapr &gt; 0)</c>
        /// (<c>:460-463</c>). It is the lip-hop, and it needs the oracle's <c>shX1/shX2</c> probe values —
        /// the port's equivalent (`HandleLedgeAhead`) is gated on the patrol state, which a boss never
        /// enters. Inventing a substitute threshold here is how the previous body produced a boss that
        /// bounced at walls.</para>
        /// </summary>
        protected virtual float ResolveJumpScale()
        {
            if (_controller == null) return 0f;

            // `if(stay && Y - begY >= 40) _loc2_ = 1;` — 40 px below the spawn anchor.
            float droppedPixels = (_spawnWorldY - transform.position.y) * TileQueryConstants.UnitToPixel;
            if (droppedPixels >= SpawnHeightReturnPixels) return 1f;

            // `if(stay && this.tr == 2 && celDY < -100) _loc2_ = 1;` — target 100 px above.
            if (_variant == 2 && _celDY > FlamerHighGroundPixels) return 1f;

            return 0f;
        }

        /// <summary>
        /// <c>jump(1)</c> (<c>:266-285</c>) — <c>aiJump = floor(30 + random*50)</c> first (the cooldown is
        /// spent even mid-air), then the impulse, then the facing shove.
        /// </summary>
        protected virtual void Jump(float scale)
        {
            _aiJump = UnitJumpMath.CooldownTicks(UnityEngine.Random.Range(0, UnitJumpMath.CooldownRollRange));

            float jumpForce = _controller.Stats != null ? _controller.Stats.JumpForce : 0f;
            if (jumpForce <= 0f) return;
            if (!_controller.IsGrounded) return;

            JumpVertical(jumpForce * scale);

            // `if(aiNapr == -1) dx *= 0.8; else dx += storona * accel * 2;` — only the rightward half is
            // expressible: the seam offers a desired speed, not a velocity, so there is nothing to damp.
            // Same recorded gap as `ArmedShooterBrain.JumpScaled`, which cites it in full.
            if (_motor != null)
            {
                float accel = _controller.Stats != null ? _controller.Stats.Acceleration : 0f;
                if (accel > 0f)
                {
                    int facing = _controller.FacingDirection >= 0 ? 1 : -1;
                    _motor.AddForce(new Vector2(facing * accel * 2f, 0f));
                }
            }
        }

        /// <summary>
        /// AS3 <c>attack()</c> (<c>:509-561</c>), branch for branch. Every weapon call is the held mount's
        /// trigger; the weapon's own <c>rapid_act</c> paces the individual rounds, exactly as in the
        /// oracle — nothing here decides when a bullet leaves.
        /// </summary>
        protected virtual void Attack()
        {
            // Release first, then let the branch below press it: `EnemyWeaponMount` latches its trigger
            // until told otherwise, so a tick that does not fire must actively let go. This is the bug
            // `ArmedShooterBrain` documents at length ("the trigger was never true while a mount ticked").
            SetWeaponTrigger(false);
            UpdateAim();

            if (_blackboard.TargetUnit == null)
            {
                // `attack()`'s four arms do not share one target guard, and the difference is real:
                //   - state 1 (:511) needs `celUnit` for `attKorp` — nothing to hit, nothing happens;
                //   - state 3 (:515-534) has <b>no</b> guard: it fires the burst at `celX/celY`, which
                //     `setCel`'s else branch has collapsed onto the boss's own centre (:4727-4732);
                //   - state 4 (:535-541) only shakes the floor, so it runs regardless;
                //   - state 5 (:548) guards the stray shot with `if(celUnit && isrnd(0.02))`.
                // Returning here for every state made a targetless boss silent in state 3 as well.
                if (_bossState == 4 && _aiTCh == BeatTick) Quake();
                else if (_bossState == 3) AttackRanged();
                return;
            }

            switch (_bossState)
            {
                case 1:
                    // `if(aiState == 1 && celUnit) attKorp(celUnit, Math.abs(dx - celUnit.dx) > 8 ? 1 : 0.5);`
                    // — a charging body hits for full scale, a standing one for half. <b>Relative</b>
                    // speed: a boss running at the same rate as a fleeing player is closing at zero and
                    // pays half. See `BossRaiderRules.ChargeContactScale`.
                    _controller.TryContactAttack(
                        _blackboard.TargetUnit,
                        BossRaiderRules.ChargeContactScale(
                            CurrentVelocityX,
                            _blackboard.TargetUnit.VelocityPixelsPerFrame.x));
                    break;

                case 3:
                    AttackRanged();
                    break;

                case 4:
                    if (_aiTCh == BeatTick) Quake();
                    break;

                case 5:
                    AttackVortex();
                    break;
            }
        }

        /// <summary>
        /// The <c>aiState == 3</c> arm (<c>:515-534</c>): the burst window, then the point-blank body hit.
        /// </summary>
        protected virtual void AttackRanged()
        {
            // `mazil = 10;` (`:517`) is the shot-spread penalty — `Weapon.as:1460` adds it, in degrees,
            // to the weapon's own `deviation`. Not modelled, for the reason `ArmedShooterBrain` records at
            // its own `mazil` line: `Unit.mazil` reaches the shot through `Pers.mazilAdd`, and an NPC in
            // this port has no stat source to put it on. Recorded here rather than invented.
            if (AttackOch > 0)
            {
                if (_aiAttackT <= 0)
                {
                    // `aiAttackT = Math.round((random*0.4 + 0.8) * aiAttackOch)` — 80-120 % of the window.
                    _aiAttackT = Mathf.RoundToInt((UnityEngine.Random.value * 0.4f + 0.8f) * AttackOch);
                }

                if (_aiAttackT > AttackOch * 0.25f)
                {
                    SetWeaponTrigger(true);
                }

                // `--aiAttackT;` — at the END of the branch, after the shot test, so the window's first
                // tick can fire.
                _aiAttackT--;
            }

            if (_celDX * _celDX + _celDY * _celDY < MeleeRangePixels * MeleeRangePixels &&
                UnityEngine.Random.value < MeleeProbeChance)
            {
                _controller.TryContactAttack(_blackboard.TargetUnit, 0.5f);
            }
        }

        /// <summary>
        /// The <c>aiState == 5</c> arm (<c>:542-560</c>): the vortex on the beat, a stray shot, and the
        /// point-blank body hit.
        /// </summary>
        protected virtual void AttackVortex()
        {
            // `if(aiTCh == 5 && this.kol_emit && this.tr == 1) this.emit();`
            if (_aiTCh == BeatTick && _kolEmit > 0 && _variant == 1)
            {
                OnVortexEmitRequested();
            }

            if (UnityEngine.Random.value < VortexStrayShotChance)
            {
                SetWeaponTrigger(true);
            }

            if (_celDX * _celDX + _celDY * _celDY < MeleeRangePixels * MeleeRangePixels &&
                UnityEngine.Random.value < MeleeProbeChance)
            {
                // `attKorp(celUnit, Math.abs(dx) > 8 ? 1 : 0.5)` (:558) — the boss's <b>own</b> drift,
                // not the closing speed. Deliberately not `ChargeContactScale`: the two oracle lines
                // differ, and this is the one that is absolute.
                _controller.TryContactAttack(
                    _blackboard.TargetUnit,
                    BossRaiderRules.VortexContactScale(CurrentVelocityX));
            }
        }

        /// <summary>
        /// <c>emit()</c> (<c>:245-252</c>) — <c>loc.createUnit("vortex", X, Y - scY/2, true)</c>, then
        /// <c>emit_t = 500; --kol_emit;</c>.
        ///
        /// <para><b>Blocked, and named rather than faked.</b> The port's runtime spawn API
        /// (<see cref="SecurityRuntime.SpawnFamilyReinforcement"/>) resolves a <i>family</i> to a variant
        /// through <c>randomCid</c>; it cannot spawn an exact unit id at a chosen point, which is what
        /// <c>createUnit("vortex", …)</c> is. An override that did nothing but decrement the counter would
        /// be a lie in the log, so the counter is only decremented by a caller that actually spawned one.
        /// </para>
        /// </summary>
        protected virtual void OnVortexEmitRequested()
        {
        }

        /// <summary>
        /// <c>quake()</c> (<c>:563-567</c>) — <c>loc.earthQuake(40)</c> plus the <c>quake</c> emitter.
        /// <b>No damage</b>: the oracle's quake is a camera shake (<c>World.w.quake(40, 10)</c>).
        /// </summary>
        protected virtual void Quake()
        {
            QuakeRequested?.Invoke(40f);
        }

        /// <summary>
        /// <c>damage()</c>'s reinforcement half (<c>:229-243</c>):
        /// <c>if(tr == 2 &amp;&amp; globalDif &gt; 1) { _loc6_ = floor((maxhp-hp)/maxhp*4); if(_loc6_ &gt; called) { loc.enemySpawn(true,true); ++called; } }</c>.
        ///
        /// <para><b>Only the flamer variant calls for help, and only above Easy.</b> Both are the
        /// oracle's conditions and both are load-bearing: a <c>tr == 1</c> boss is a pure gunner with no
        /// reinforcement budget at all, and on difficulty 0-1 the quarter-HP event is silent.</para>
        ///
        /// <para><b>One timing divergence, convergent, recorded rather than emulated.</b> The oracle tests
        /// this once per <i>damage event</i>: a single hit that crosses two quarters spawns <b>one</b>
        /// reinforcement and leaves <c>called</c> one behind, so the second arrives on the next hit that
        /// reports the same <c>_loc6_</c>. Here <see cref="BossBrain.CheckQuarterHpThresholds"/> walks
        /// every newly crossed quarter in one tick, so such a hit spawns two. The totals converge — the
        /// oracle always catches up, because <c>_loc6_ &gt; called</c> stays true — and the budget
        /// (<see cref="RoomAlarm.SpawnBudget"/>) caps both. Emulating the lag exactly would need a per-tick
        /// latch plus carried debt, because this hook cannot re-fire on a quarter that
        /// <c>CheckQuarterHpThresholds</c> has already counted; a half-emulation would lose the debt
        /// outright, which is worse than front-loading it.</para>
        /// </summary>
        protected override void OnQuarterHpPassed(int quarter)
        {
            base.OnQuarterHpPassed(quarter);

            // `if(tr == 2 && globalDif > 1) { _loc6_ = floor((maxhp-hp)/maxhp*4); if(_loc6_ > called) … }`
            // (:233-241). The gate is `BossRaiderRules.ShouldCallReinforcement` so the three terms are
            // pinned by a fixture; `_called` is written only when the spawn actually happened, which is
            // the oracle's `++called` after `loc.enemySpawn(...)`.
            if (!BossRaiderRules.ShouldCallReinforcement(
                    _variant, GlobalDifficulty.Value, quarter, _called))
            {
                return;
            }

            BossRaiderController controller = RaiderController;
            if (controller == null) return;

            if (controller.SpawnReinforcementWave(quarter))
            {
                _called = quarter;
            }
        }

        /// <summary>
        /// The <c>setCel</c> half of <c>control()</c> — AS3 polls the target every
        /// <see cref="CelCadence"/> ticks and writes <c>celX</c>/<c>celY</c> unconditionally
        /// (<c>:340-350</c>, <c>Unit.as:4703-4734</c>).
        ///
        /// <para><b>Why this is a poll of its own rather than the blackboard's deltas.</b>
        /// <c>EnemyBlackboard.TargetDeltaX/Y</c> are written <i>only on a sighting commit</i>
        /// (<c>EnemySensors.cs</c>), so they freeze the moment line of sight breaks. AS3's <c>setCel</c>
        /// writes every time it is asked, so a boss that loses sight of the player keeps walking at the
        /// last place it looked. Reading the blackboard here would give a boss that stops dead the
        /// instant a pillar comes between them — the same defect the griffin's flight had.</para>
        /// </summary>
        protected virtual void RefreshCel()
        {
            UnitController target = _blackboard.TargetUnit;
            if (target != null)
            {
                // `celX = param1.X + param1.scX / 4 * param1.storona; celY = param1.Y - param1.scY / 2;`
                // (`Unit.as:4707-4708`). The vertical term is the body's centre; the horizontal term is
                // a quarter-width bias toward the side the target faces, so a raider looking away is
                // aimed slightly behind its middle. Both are needed: `celDX` is compared against 100
                // and 200 px and against `aiDist`, so a 30 px bias on a 120 px-wide boss moves real
                // thresholds. `EnemyAimMath.TargetCel` is the one implementation of the pair.
                Vector2 cel = EnemySensors.GetUnitAimPixels(target);
                _celX = cel.x;
                _celY = cel.y;
                return;
            }

            // <b>`setCel`'s else branch, NOT the `enemyAct` ahead-point.</b> `celX = X; celY = Y - scY / 2;
            // celUnit = null;` (`Unit.as:4727-4732`) — the goal collapses onto the unit's <i>own</i>
            // centre when the player is not `isMeet`. The similar-looking `celY = Y - scY; celX = X +
            // scX * storona * 2;` at `:308-309` belongs to the `enemyAct <= 0` early return, a different
            // rule, and using it here would send a targetless boss marching forward forever.
            //
            // The observable difference is the whole "stand and look around" state: with the goal on its
            // own centre `celDX` is 0, so the `|celDX| > aiDist` arms stop closing and the 2/3 arms hand
            // back to state 1 within 200 px. With the ahead-point the boss never idles.
            UnitDefinition def = _controller != null ? _controller.Definition : null;
            float heightPx = def != null && def.Height > 0f ? def.Height * 100f : 50f;

            Vector2 feet = (Vector2)_controller.transform.position * TileQueryConstants.UnitToPixel;

            _celX = feet.x;
            _celY = feet.y + heightPx * 0.5f;
        }

        /// <summary>
        /// <c>celDX = celX - X; if(stay) celDY = celY - Y + scY;</c> (<c>:351-355</c>). Both are pixels;
        /// <c>celDY</c> is therefore target-centre minus own-centre, which is why the oracle adds
        /// <c>scY</c> back to the feet-origin <c>Y</c>.
        /// </summary>
        protected virtual void UpdateCelDeltas()
        {
            if (_controller == null) return;

            Vector2 feet = (Vector2)_controller.transform.position * TileQueryConstants.UnitToPixel;
            UnitDefinition def = _controller.Definition;
            float heightPx = def != null && def.Height > 0f ? def.Height * 100f : 50f;

            // `celDX = celX - X;` — assigned every frame (`:351`).
            _celDX = _celX - feet.x;

            // `if(stay) { celDY = celY - Y + scY; }` (`:352-355`). `stay` is the grounded flag, so an
            // <i>airborne</i> boss keeps the value it had when it left the ground. Recomputing it in the
            // air would re-aim `throu` and `aiVNapr` mid-jump, which the oracle never does — and the
            // jump triggers that read them are themselves gated on `stay`.
            if (_controller.IsGrounded)
            {
                _celDY = _celY - feet.y - heightPx;
            }

            // `if(celDY > 40) aiVNapr = 1; else if(celDY < -40) aiVNapr = -1; else aiVNapr = 0;`
            // (<c>:356-367</c>). AS3's Y grows downward, so a target *below* is a positive delta there.
            if (_celDY < -VerticalDeadBandPixels) _aiVNapr = 1;
            else if (_celDY > VerticalDeadBandPixels) _aiVNapr = -1;
            else _aiVNapr = 0;
        }

        /// <summary>
        /// AS3's <c>dx</c>, in px/frame — the value <c>animate()</c> tests and <c>attack()</c> scales by.
        /// Read from the motor when there is one, otherwise from the controller's own AS3-unit velocity,
        /// because <c>RoomUnitSpawner</c> picks exactly one of the two movement layers.
        /// </summary>
        protected float CurrentVelocityX =>
            _motor != null ? _motor.State.Velocity.x : _controller.VelocityPixelsPerFrame.x;

        /// <summary>
        /// AS3 <c>aiVNapr</c> — <c>1</c> when the goal is below, <c>-1</c> above, <c>0</c> level. Exposed
        /// because the jump rules are the only consumer and a reader of this file needs to check the sign
        /// against <c>:356-367</c>.
        /// </summary>
        public int VerticalApproach => _aiVNapr;

        protected virtual void SetWeaponTrigger(bool firing)
        {
            EnemyWeaponMount mount = RaiderController?.WeaponMount;
            if (mount == null) return;

            if (firing && _blackboard.TargetUnit != null)
            {
                // The target's `cel` in world units — the same point `RefreshCel` keeps in `_celX`/
                // `_celY`: `celX = X + scX/4 * storona`, `celY = Y - scY/2` (`Unit.as:4707-4708`).
                // One rule, one implementation (`EnemyAimMath.TargetCel`), so the barrel and the
                // movement goal cannot drift apart.
                mount.SetAimTarget(EnemySensors.GetUnitAimWorld(_blackboard.TargetUnit));
            }

            mount.SetFiring(firing);
        }

        /// <summary>
        /// <para><b>The hold point is the mount's, not this unit's.</b> <c>setWeaponPos</c>
        /// (<c>:193-197</c>) pins the boss's weapon to <c>weaponY = Y - scY * 0.58</c> — 58 % of its height,
        /// chest height on a 120 px sprite. <c>EnemyWeaponMount.ResolveHoldPoint</c> already resolves a hold
        /// point for every armed enemy through <c>WeaponHoldPointMath.Resolve</c> or an
        /// <c>IWeaponHoldPointSource</c> bone, and this unit rides that convention rather than overriding
        /// it; the 0.58 constant is therefore not applied separately here. If the flamer is ever seen to
        /// float above or sink into the boss's hands, that ratio is the first thing to check.</para>
        ///
        /// Keep the barrel on the goal every tick — AS3 writes <c>currentWeapon.rot</c> every frame from
        /// <c>celX</c>/<c>celY</c> (<c>UnitBossRaider.as:193-197</c> sets the hold point; the aim itself is
        /// the weapon's own <c>drot</c> pass). Without this the gun snaps back to the mount's
        /// facing-only fallback between shots, which is the reported "its weapon does not turn".
        /// </summary>
        protected virtual void UpdateAim()
        {
            if (_controller == null || !_controller.IsAlive) return;

            EnemyWeaponMount mount = RaiderController?.WeaponMount;
            if (mount == null) return;

            mount.SetAimTarget(new Vector2(_celX, _celY) * TileQueryConstants.PixelToUnit);
        }

        /// <summary>
        /// <c>UnitBossRaider.animate()</c> (<c>:133-191</c>), branch for branch.
        ///
        /// <para><b>This is the other half of "no animation".</b> The previous body returned
        /// <c>"stay"</c> for every living state, so a 210-frame clip was played as its first 20 idle
        /// frames forever: no run, no trot, no jump, and no <c>bac</c> (the quake wind-up).</para>
        ///
        /// <para><b>One sub-clip of the airborne arm is not ported.</b> The oracle then does
        /// <c>vis.osn.body.gotoAndStop(Math.round(16 + dy))</c> clamped to <c>1..32</c>
        /// (<c>:180-189</c>) — a <b>32-cell</b> pose row on the body child clip, indexed by vertical
        /// velocity, so the boss's legs track the arc. That is <c>osn.body</c>
        /// (<c>DefineSprite_2030</c>), a different clip from the 16-frame <c>jump</c> row this method
        /// selects (<c>firstFrame: 77</c>, <c>length: 16</c>), and <c>UnitAnimator.SetStab</c> poses the
        /// <i>current state's</i> row rather than a child clip — the seam <c>ZombieBrain</c> uses works
        /// there only because the zombie's oracle poses its own jump row (<c>dy * 0.6 + 8</c> over 16
        /// cells, <c>UnitJumpMath.JumpPoseProgress</c>). Reusing it here would play a 16-cell row with
        /// 32-cell indices, which is a different animation, not a closer one. Left unported and named;
        /// the unit still changes to the <c>jump</c> row, so the arc is drawn.</para>
        /// </summary>
        protected override string ResolveAnimState()
        {
            if (_controller == null) return null;

            // The ladder itself is `BossRaiderRules.ResolveAnimState` — it is the branch order that
            // used to return "stay" for every living state, so it belongs where a fixture can pin it
            // against `animate():133-191` rather than inside a MonoBehaviour.
            //
            // `stay` in the oracle is the grounded flag, and `dx` is px/frame. `CurrentVelocityX`
            // reads the motor when there is one and the controller's AS3-unit velocity otherwise,
            // because `RoomUnitSpawner` installs exactly one of the two movement layers.
            return BossRaiderRules.ResolveAnimState(
                _controller.IsAlive,
                _bossState,
                _controller.IsGrounded,
                CurrentVelocityX);
        }

        /// <summary>
        /// The abstract the base state machine requires. The boss never enters
        /// <see cref="EnemyAIState.Attack"/> (its ladder is <see cref="TickControl"/>), so this is only
        /// reachable if a future caller drives the family ticks — where the right answer is still the
        /// oracle's <c>attack()</c>.
        /// </summary>
        protected override void ExecuteAttackAction()
        {
            Attack();
        }
    }
}
