using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Campaign;
using PFE.Systems.Combat;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Weapons;
using UnityEngine;

namespace PFE.Entities.Enemies.Bosses
{
    /// <summary>
    /// The Ultra Sentinel — the boss of the Stable 88 core (<c>rooms_core/bossultra</c>) — as a port of
    /// AS3 <c>UnitBossUltra</c> (<c>fe/unit/UnitBossUltra.as</c>): <c>control():255-446</c>,
    /// <c>attack():457-498</c>, <c>castShit():448-455</c>, <c>animate():193-220</c> and
    /// <c>dropLoot():105-111</c>.
    ///
    /// <para><b>What this replaced, and why it had to be replaced rather than tuned.</b> The previous
    /// class was an invention wearing the oracle's name: it chased the player, fired every 45 ticks
    /// (25 in "usil"), and dealt damage <i>directly</i> through <see cref="PendingDamage.Direct"/> with
    /// a <see cref="DamageType.Laser"/> contact context. None of that is in <c>UnitBossUltra</c>. The
    /// real fight is:</para>
    ///
    /// <list type="number">
    ///   <item><description><b>A three-step timer rotation, not a perception ladder.</b> <c>aiState</c>
    ///     cycles 1→2→3→1 on <c>aiTCh</c>: 1 flies to one of five fixed arena points, 2 rolls which
    ///     weapon to use, 3 fires it. The boss never chases and never reacts to being shot
    ///     at.</description></item>
    ///   <item><description><b>Five authored arena points, re-rolled rather than walked.</b> State 1
    ///     picks <c>floor(random*5)</c> and only nudges when it would repeat, so the sentinel can cross
    ///     the whole room in one reposition — which is why that state gets a 60-tick budget for a trip
    ///     that can be 30 tiles long.</description></item>
    ///   <item><description><b>Four weapons behind one <c>attState</c>.</b> 0 rockets
    ///     (<c>robomlau2</c>), 1 gatling (<c>robogatp</c>, then <c>robogatp2</c> after the phase change),
    ///     2 grenades (<c>roboplagr</c>, thrown along a <c>forceRot</c> that sweeps 0.1 rad per tick) and
    ///     4 gas (<c>robogas</c>). The rolls are asymmetric: only the 0.33 gas roll produces 4 and only
    ///     the low-ceiling arm produces 2, so <c>Math.floor(random*2)</c> never reaches either.</description></item>
    ///   <item><description><b>A shield pool, twice.</b> <c>castShit()</c> refills 500 points on every
    ///     reposition, but <i>only</i> on Hard (and Very Hard below half HP). At half HP the boss
    ///     overwrites it with 2000 and hides its first gatling — the phase change.</description></item>
    ///   <item><description><b>A 150-tick death.</b> <c>sost == 2</c> explodes at a ramping
    ///     <c>isrnd(0.3 - timerDie/500)</c> for two and a half seconds before the body goes.</description></item>
    /// </list>
    ///
    /// <para><b>Why <c>SimTick</c> does not call <c>base</c>.</b> <see cref="EnemyBrain.SimTick"/> is a
    /// perception-driven Idle/Patrol/Alert/Chase machine and this boss has no such ladder — its
    /// <c>aiState</c> is a <i>counter</i>, and <c>aiState == 1</c> means "fly to a point", not "heard
    /// something". Running the base machine and overriding <c>TickCombatChase</c> (what the previous
    /// class did) left two state machines writing the same fields. Perception is still run, because the
    /// aim gate needs a target and so does the contact attack — but its <i>state transitions</i> are
    /// not consulted.</para>
    ///
    /// <para><b>Every coordinate in this file is AS3 room-local pixels</b> — x from the left wall,
    /// <b>y downward from the ceiling</b>. That is deliberate rather than lazy: the oracle's own
    /// comparisons (<c>Y &lt; 17*40</c>, <c>celY &gt; 16*40</c>, <c>moveY = y*40+40</c>) only mean what
    /// they meant in the original if they are evaluated in the original's own space, and the arithmetic
    /// lives in <see cref="BossUltraRules"/>, which is Unity-free and pinned by an offline fixture. The
    /// Y flip happens in exactly two places — <see cref="ToAs3"/> and <see cref="ToWorld"/> — and the
    /// vertical thrust is negated once in <see cref="ApplyFlight"/>.</para>
    /// </summary>
    public class BossUltraBrain : BossBrain
    {
        // ── the five weapons, in the oracle's constructor (:71-103) ───────────────────────────────
        //
        // The constructor hard-codes these ids; the unit row's `<w>` list (AllData.as:1572-1575) happens
        // to name four of them and omits robogatp2, which is built as `currentWeapon2` and stays hidden
        // until the phase change. As with the alicorn, the row describes what the unit *drops*, not
        // what it fires, so the loadout is taken from the code.

        /// <summary><c>currentWeapon</c> — the gatling, fired by <c>attState == 1</c> before the phase
        /// change (<c>:75</c>).</summary>
        public const string WeaponGatling = "robogatp";

        /// <summary><c>currentWeapon2</c> — the upgraded gatling, hidden until half HP (<c>:76</c>).</summary>
        public const string WeaponGatlingEnhanced = "robogatp2";

        /// <summary><c>dopWeapon</c> — the rocket pod, fired by <c>attState == 0</c> (<c>:77</c>).</summary>
        public const string WeaponRockets = "robomlau2";

        /// <summary><c>gasWeapon</c> — fired by <c>attState == 4</c> (<c>:78</c>).</summary>
        public const string WeaponGas = "robogas";

        /// <summary>
        /// <c>thWeapon</c> — the grenade launcher, fired by every other <c>attState</c> (<c>:79</c>).
        /// Built with <c>findCel = false</c> and <c>kolAmmo = 100000</c>: it does not track the player,
        /// it throws along <see cref="ForceRot"/>, and it never runs dry.
        /// </summary>
        public const string WeaponGrenades = "roboplagr";

        /// <summary>Every weapon this boss mounts. See <see cref="WeaponGatling"/>'s remarks.</summary>
        public static readonly string[] WeaponIds =
        {
            WeaponGatling, WeaponGatlingEnhanced, WeaponRockets, WeaponGas, WeaponGrenades,
        };

        /// <summary>
        /// <c>thWeapon.forceRot += 0.1</c> (<c>:490</c>) — the grenade launcher's lob angle, in radians
        /// per tick of the grenade attack state.
        /// </summary>
        /// <remarks>
        /// <b>It accumulates for the whole fight.</b> AS3 never resets it, so the second grenade volley
        /// resumes the sweep where the first left off rather than starting over — which is what makes
        /// the barrage feel like a rotating hose instead of the same arc four times. Reset would be a
        /// "fix" for a non-bug.
        /// </remarks>
        public const float GrenadeRotationPerTick = 0.1f;

        // ── the oracle's fields ───────────────────────────────────────────────────────────────────

        /// <summary><c>aiState</c> (<c>:22</c>) — the rotation step, 0..3. <c>0</c> is only ever the
        /// constructor's initial value.</summary>
        [SerializeField] int _aiState;

        /// <summary><c>attState</c> (<c>:23</c>) — which weapon the current beat fires.</summary>
        [SerializeField] int _attState;

        /// <summary><c>aiTCh</c> (<c>:89</c>, initial 80) — ticks until the next rotation step.</summary>
        [SerializeField] int _aiTCh = BossUltraRules.InitialAiTCh;

        /// <summary><c>mp</c> (<c>:59</c>) — the arena point the boss is standing on.</summary>
        [SerializeField] int _movePoint = BossUltraRules.InitialMovePointIndex;

        /// <summary><c>t_turn</c> (<c>:67</c>) — ticks left before the facing flips.</summary>
        [SerializeField] int _tTurn = BossUltraRules.TurnDelayTicks;

        /// <summary><c>t_shit</c> (<c>:69</c>) — the shield recast timer, decremented at <c>:279-282</c>
        /// and read by <c>castShit()</c>.</summary>
        [SerializeField] int _tShit = BossUltraRules.InitialShieldTimer;

        /// <summary><c>usil</c> (<c>:31</c>) — the phase-two flag, set once at half HP.</summary>
        [SerializeField] bool _usil;

        /// <summary><c>thWeapon.forceRot</c> — see <see cref="GrenadeRotationPerTick"/>.</summary>
        [SerializeField] float _forceRot;

        /// <summary><c>moveX</c>/<c>moveY</c> (<c>:61-62</c>) in AS3 room-local pixels.</summary>
        Vector2 _moveAs3;

        /// <summary><c>celX</c>/<c>celY</c> in AS3 room-local pixels — the point the weapons aim at.</summary>
        Vector2 _celAs3;

        /// <summary>Whether <see cref="_celAs3"/> has ever been written. AS3's <c>celX</c> starts at
        /// <c>0</c> and is written by the first <c>setCel</c>; the difference matters only for the
        /// first frame, and a flag says so instead of aiming at the room's top-left corner.</summary>
        bool _hasCel;

        /// <summary><c>timerDie</c> counting <i>down</i> from
        /// <see cref="BossUltraRules.DeathTimerTicks"/> — the death-throes clock (<c>Unit.as:1792</c>).</summary>
        int _deathThroes;

        /// <summary>The last EMP vulnerability this brain wrote, so the table is only rebuilt when the
        /// shield actually changes state.</summary>
        float _empVulnerabilityWritten = float.NaN;

        // ── readbacks (the debug overlay and the fixtures) ────────────────────────────────────────

        public int AiState => _aiState;
        public int AttackState => _attState;
        public int AiTCh => _aiTCh;
        public int MovePointIndex => _movePoint;
        public int ShieldTimerTicks => _tShit;
        public int DeathThroesTicks => _deathThroes;
        public bool Usil => _usil;
        public float ForceRot => _forceRot;

        /// <summary>The arena point being flown to, in AS3 room-local pixels.</summary>
        public Vector2 MovePointPixels => _moveAs3;

        /// <summary>The aim point, in AS3 room-local pixels.</summary>
        public Vector2 AimPointPixels => _celAs3;

        /// <summary>
        /// AS3 <c>shithp</c>. Answered from the <b>controller's</b> pool
        /// (<see cref="UnitController.ShieldHp"/>, i.e. <c>UnitStats.ShitHp</c>) rather than from a field
        /// here, so the damage resolver, the dome overlay and this readback all see one number.
        /// </summary>
        public float ShieldHp => Ultra != null ? Ultra.ShieldHp : 0f;

        /// <summary>The current shield's capacity, from the controller's <c>shitMaxHp</c>.</summary>
        public float ShieldMaxHp => Ultra != null ? Ultra.ShieldMaxHp : 0f;

        /// <summary>This unit as its own controller, or <c>null</c> on a bare spawn.</summary>
        protected BossUltraController Ultra => _controller as BossUltraController;

        /// <summary>This unit as an <see cref="EnemyController"/> — the weapon-mount lookup.</summary>
        protected EnemyController Enemy => _controller as EnemyController;

        /// <summary>The room, or <c>null</c> outside one.</summary>
        protected RoomInstance Room => _controller != null ? _controller.Room : null;

        /// <summary>Room height in tiles, for the AS3 Y mirror. <c>0</c> outside a room.</summary>
        int RoomHeightTiles => Room != null ? Room.height : 0;

        /// <summary>The room's origin in world pixels — <c>ITileQueryService.OriginPixel</c>.</summary>
        Vector2 OriginPixel => _tileQuery != null ? _tileQuery.OriginPixel : Vector2.zero;

        // ── construction ──────────────────────────────────────────────────────────────────────────

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
        /// The oracle's constructor speeds — <c>walkSpeed = plavSpeed = maxSpeed = 4.5</c>
        /// (<c>&lt;move speed='4.5'&gt;</c>). The base's three speed fields are all the same number
        /// because the boss has no run, no walk and no patrol; its locomotion is
        /// <see cref="ApplyFlight"/>.
        /// </summary>
        protected virtual void ApplyTuning()
        {
            if (_controller == null || _controller.Definition == null) return;

            float speed = _controller.Definition.moveSpeed > 0f ? _controller.Definition.moveSpeed : 4.5f;
            _patrolSpeed = speed;
            _alertSpeed = speed;
            _chaseSpeed = speed;

            // `<param overlook='1'>` (AllData.as:1570) — `Unit.findCel` passes `this.overLook || param1`
            // to `look()`, so a boss authored to overlook skips the facing cone AND the vertical-angle
            // penalty and acquires through 360°. **Supplied here rather than read from the asset**
            // because `bossultra.asset` carries `canOverlook: 0`: the unit importer never reads
            // `<param overlook>`, so the data is silently lost for every unit in the game. Recorded in
            // docs/OnEnemiesAndAi/33_BossUltra_Stable88_2026-10-30.md.
            _sensors.Omnidirectional = true;
        }

        // ── the tick ─────────────────────────────────────────────────────────────────────────────

        public override void SimTick(int tickIndex)
        {
            if (_controller == null)
            {
                return;
            }

            // `if(sost == 3) return;` (:258-261) — a unit destroyed outright has no body to drive. The
            // port has no `sost`, so `IsAlive == false` is the union of AS3's 2 and 3 and the two arms
            // are split by whether the death throes have run out.
            if (!_controller.IsAlive)
            {
                TickDeathThroes();
                UpdateAnimation();
                return;
            }

            // `if(World.w.enemyAct <= 0) { celY = Y - scY; celX = X + scX*storona*2; return; }`
            // (:273-277). Note what it does NOT do: it writes the aim and returns, so the boss keeps
            // hovering on the velocity it had and its weapons keep stepping. Only the decision half
            // stops — the same split BossAlicornBrain documents.
            if (EnemyActivity.IsFrozen)
            {
                _celAs3 = ToAs3(_controller.transform.position) +
                          new Vector2(_controller.SpriteSizePixels.x * _controller.FacingDirection * 2f,
                                      -_controller.SpriteSizePixels.y);
                _hasCel = true;
                PublishAim();
                UpdateAnimation();
                TickWeapons();
                return;
            }

            UpdatePerception(tickIndex);

            TickControl();

            // BossBrain's own tail. No arena rect and no quarter-HP event is authored for this boss, so
            // both are no-ops today — but they are the hooks a scripted encounter plugs into.
            CheckQuarterHpThresholds();

            UpdateAnimation();
            TickWeapons();
            ClampToArenaBounds();
        }

        /// <summary>
        /// <c>control():255-446</c>, in the oracle's order. Every line below carries its source line;
        /// the ones without are the port's substitutions and say so.
        /// </summary>
        void TickControl()
        {
            // `--t_replic;` (:269) — the taunt-text clock. `Unit.replic` (Unit.as:4813-4850) emits a
            // `replic` particle carrying a localised string above the boss, and `UnitBossUltra` calls it
            // once per attack beat (:344). **Not ported**: the port has no text-particle pipeline and no
            // `replic` particle id, and the payload is pure flavour. `t_replic` therefore has no reader
            // and is not modelled — a field written and never read is the decoy this port already has
            // four of.

            // `if(this.t_shit > 0) { --this.t_shit; }` (:279-282). Floored at zero by the guard, unlike
            // the alicorn's.
            if (_tShit > 0) _tShit--;

            // `vulner[Unit.D_EMP] = shithp > 0 ? 0.2 : 1;` (:284) — recomputed every tick, because the
            // pool can go to zero at any moment and the EMP resistance has to follow it down.
            SyncEmpVulnerability();

            // `if(aiTCh > 0) --aiTCh; else { …the rotation… }` (:285-345).
            if (_aiTCh > 0)
            {
                _aiTCh--;
            }
            else
            {
                AdvanceRotation();
            }

            // `if((aiState == 1 || aiState > 1 && attState == 1) && aiTCh % 10 == 1) setCel(loc.gg);`
            // (:367-370). Not every tick — see BossUltraRules.RefreshesAimThisTick for why the
            // exceptions are what make the rockets dodgeable.
            if (BossUltraRules.RefreshesAimThisTick(_aiState, _attState, _aiTCh))
            {
                Aim();
            }

            // `if(aiState == 0) {…} else if(aiState == 1) {…} else if(aiState >= 2) {…}` (:371-403).
            ApplyFlight();

            // `if(aiState == 2 && aiTCh % 5 == 1) { …Emitter.emit… }` (:405-418).
            if (_aiState == 2 && _aiTCh % BossUltraRules.TelegraphModulus == 1)
            {
                EmitTelegraph();
            }

            // `if(aiState > 0 && !(aiState == 3 && attState == 2)) { …turn… }` (:420-436).
            TickFacing();

            // `this.attack();` (:437).
            Attack();

            // `if(!this.usil && hp < maxhp / 2) { … }` (:438-445) — the last thing in control(), after
            // the attack. So the tick that drops the boss below half HP still fires the old weapon once.
            if (BossUltraRules.ShouldEnterPhaseTwo(_usil, CurrentHpFraction))
            {
                EnterEnhancedPhase();
            }

            // `setWeaponPos()` (:240-244) writes `weaponX/weaponY` from `vis` every frame, and
            // `Weapon.actions()` then reads `celX/celY`. The port's mounts hold their own aim, so this
            // is where it is published. **After** the attack, so a beat's `forceRot` is already advanced.
            PublishAim();
        }

        // ── the rotation (:285-345) ──────────────────────────────────────────────────────────────

        /// <summary>One expiry of <c>aiTCh</c> — the oracle's three-arm ladder.</summary>
        void AdvanceRotation()
        {
            _aiState = BossUltraRules.AdvanceState(_aiState, _attState);

            if (_aiState == 1)
            {
                AdvanceMoveStep();
            }
            else if (_aiState == 2)
            {
                AdvanceAimStep();
            }
            else if (_aiState == 3)
            {
                AdvanceAttackStep();
            }
        }

        /// <summary>
        /// Step 1 (<c>:303-320</c>): pick a new arena point and reset the shield timer.
        /// </summary>
        /// <remarks>
        /// <b>The point is re-rolled, not walked.</b> <c>floor(random*5)</c> is uniform over the five, so
        /// consecutive repositions are independent — the sentinel can go from the left corner to the
        /// right one and back. The only constraint is that it may not pick the point it is already on.
        /// </remarks>
        void AdvanceMoveStep()
        {
            _movePoint = BossUltraRules.NextMovePointIndex(_movePoint, UnityEngine.Random.value);
            _moveAs3 = BossUltraRules.MovePointPixels(_movePoint);

            _aiTCh = BossUltraRules.MoveStateTicks;

            // `this.castShit();` (:319) — once per reposition, and only on the difficulties the rules
            // class names.
            CastShield();
        }

        /// <summary>
        /// Step 2 (<c>:321-341</c>): roll the attack state, then the aim-state length.
        /// </summary>
        /// <remarks>
        /// <para><b>Three draws, in the oracle's order, and the third is conditional.</b> AS3 writes
        /// <c>isrnd(0.33)</c> for the gas arm, <c>Math.floor(random*2)</c> for the 0/1 arm, and then a
        /// <i>third</i> <c>isrnd(0.33)</c> for the low-ceiling arm — but that one is inside
        /// <c>Y &lt; 680 &amp;&amp; celY &gt; 640 &amp;&amp; …</c>, so <c>&amp;&amp;</c>'s short-circuit
        /// means it is only drawn when the boss is above the line and the player below it. Drawing it
        /// unconditionally would be a different (still fair) sequence, and this port does not do that.</para>
        ///
        /// <para><b><c>attState == 2</c> is the grenade state, not a telegraph.</b> The telegraph is the
        /// particle emit in <see cref="TickControl"/>, which fires for 0/1/4 only. State 2 is reached
        /// <i>solely</i> from the low-ceiling arm, which also cuts the aim to 5 ticks — so the boss
        /// snaps to a 120-tick grenade barrage almost immediately, and only while it is high and the
        /// player is low.</para>
        /// </remarks>
        void AdvanceAimStep()
        {
            // `if(Y < 17*40 && celY > 16*40 && isrnd(0.33))` — the first two terms first, because the
            // oracle's `&&` means the roll is not drawn when they fail.
            Vector2 selfAs3 = ToAs3(_controller.transform.position);
            bool lowCeilingTermsHold = _hasCel &&
                                       selfAs3.y < BossUltraRules.LowCeilingBossY &&
                                       _celAs3.y > BossUltraRules.LowCeilingTargetY;

            float lowCeilingRoll = lowCeilingTermsHold ? UnityEngine.Random.value : 0f;

            _attState = BossUltraRules.RollAttackState(
                _attState,
                UnityEngine.Random.value,
                UnityEngine.Random.value,
                lowCeilingRoll,
                lowCeilingTermsHold);

            bool lowCeilingArmFired =
                lowCeilingTermsHold && lowCeilingRoll < BossUltraRules.AttackStateRollChance;

            _aiTCh = BossUltraRules.AimTicksFor(_attState, lowCeilingArmFired);

            // `if(this.attState == 0) { setCel(loc.gg); }` (:338-341) — the rocket beat re-aims on
            // entry, on top of the `aiTCh % 10 == 1` rule, because a rocket volley never re-aims again
            // once it starts (attState != 1 fails that rule's second term).
            if (_attState == 0)
            {
                Aim();
            }
        }

        /// <summary>Step 3 (<c>:342-365</c>): the attack-state length.</summary>
        void AdvanceAttackStep()
        {
            _aiTCh = BossUltraRules.AttackTicksFor(_attState, UnityEngine.Random.value);
        }

        // ── flight (:371-403) ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The oracle's three-arm locomotion, with the thrust applied through the flyer seam so the
        /// tile motor and the motor-less path both see it.
        /// </summary>
        /// <remarks>
        /// <para><b>Why <c>ApplyFlightBrakeToPixels</c> is called here as well as in the physics
        /// step.</b> AS3 runs <c>forces()</c> before <c>control()</c>, so the speed cap and the brake
        /// deadzone act on the velocity the <i>previous</i> frame left behind and this frame's thrust is
        /// added afterwards. The port's brain runs at order 24 and the motor at 25, so without this call
        /// the thrust would be added first and then braked — which zeroes any thrust smaller than
        /// <c>brake</c> (0.3) and would make the sentinel's final approach stutter. This is the same
        /// ordering correction <c>FlyerBrain.Fly</c> documents.</para>
        ///
        /// <para><b>The damping is a multiply of the whole velocity, not of the thrust.</b>
        /// <c>dx *= 0.8</c> is the oracle's own text, and it is what stops the sentinel dead at its
        /// point instead of orbiting it: inside 1000 px² the thrust is already shrinking with the
        /// square of the distance (<see cref="BossUltraRules.SeekLimit"/>), and the multiply then takes
        /// a slice off whatever velocity is left.</para>
        /// </remarks>
        void ApplyFlight()
        {
            Vector2 velocity = _controller.ApplyFlightBrakeToPixels(_controller.VelocityPixelsPerFrame, 1f);

            if (_aiState == 1)
            {
                // `spd.x = moveX - X; spd.y = moveY - Y; norma(spd, min(accel, accel*d²/10000));`
                // (:390-392).
                Vector2 selfAs3 = ToAs3(_controller.transform.position);
                Vector2 toPoint = _moveAs3 - selfAs3;
                float distanceSquared = toPoint.x * toPoint.x + toPoint.y * toPoint.y;

                float acceleration = _controller.Definition != null
                    ? _controller.Definition.acceleration
                    : 2.5f;
                if (acceleration <= 0f) acceleration = 2.5f;

                Vector2 thrust = BossUltraRules.ClampLength(
                    toPoint, BossUltraRules.SeekLimit(distanceSquared, acceleration));

                // AS3's y grows down and the port's grows up, so the vertical thrust is mirrored once,
                // here. `thrust.x` needs no flip.
                velocity.x += thrust.x;
                velocity.y -= thrust.y;

                float damp = BossUltraRules.DampFactor(distanceSquared, _aiState);
                velocity *= damp;
            }
            else if (_aiState >= 2)
            {
                // `dx *= 0.7; dy *= 0.7;` (:399-403) — the attacking boss bleeds its speed off and hangs
                // in the air. No thrust at all: it is aiming, not flying.
                velocity *= BossUltraRules.DampFactor(0f, _aiState);
            }

            // `aiState == 0` deliberately does nothing to `dx`/`dy` (:376-388) — it only sets `storona`
            // from the drift, which TickFacing does. The sentinel starts in state 0 for exactly one
            // `aiTCh` (80 ticks) and then never returns to it.

            _controller.SetVelocityPixelsPerFrame(velocity);
            MoveHorizontal(velocity.x);
            SetVerticalSpeed(velocity.y);
        }

        // ── the telegraph (:405-418) ─────────────────────────────────────────────────────────────

        /// <summary>
        /// The five-tick wind-up particles, one family per weapon.
        /// </summary>
        /// <remarks>
        /// <para><b>The offsets are the oracle's, and they are not symmetric.</b> <c>laser</c> scatters
        /// ±50/±50, <c>plasma</c> ±25/±20 and <c>spark</c> ±50/±50 — but the vertical half is written
        /// <c>celY - random*50</c>, i.e. it only ever goes <i>upward</i> in AS3's y-down space. That is a
        /// wind-up that rises toward the muzzle, not a symmetric puff, so it is reproduced as written
        /// rather than "centred".</para>
        ///
        /// <para><b><c>attState == 2</c> emits nothing.</b> The grenade beat has no telegraph — the
        /// shells themselves are the warning.</para>
        /// </remarks>
        void EmitTelegraph()
        {
            if (!_hasCel) return;

            float x;
            float y;
            string id;

            if (_attState == 0)
            {
                id = "laser";
                x = _celAs3.x + UnityEngine.Random.value * 100f - 50f;
                y = _celAs3.y - UnityEngine.Random.value * 50f;
            }
            else if (_attState == 1)
            {
                id = "plasma";
                x = _celAs3.x + UnityEngine.Random.value * 50f - 25f;
                y = _celAs3.y - UnityEngine.Random.value * 20f;
            }
            else if (_attState == 4)
            {
                id = "spark";
                x = _celAs3.x + UnityEngine.Random.value * 100f - 50f;
                y = _celAs3.y - UnityEngine.Random.value * 50f;
            }
            else
            {
                return;
            }

            EmitAtAs3(id, new Vector2(x, y));
        }

        // ── facing (:420-436) ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The 15-tick turn commitment, with the grenade beat's exemption.
        /// </summary>
        /// <remarks>
        /// <para><b><c>aiState == 3 &amp;&amp; attState == 2</c> does not turn.</b> The grenade launcher
        /// fires along <c>forceRot</c> rather than at the player, so turning to face him would swing the
        /// barrel for no reason — the oracle freezes the facing for the whole 120-tick barrage.</para>
        ///
        /// <para><b>State 0 follows the drift.</b> The boss starts there and never returns, so this arm
        /// is the one that decides which way it is looking when the fight opens.</para>
        /// </remarks>
        void TickFacing()
        {
            if (_aiState == 0)
            {
                float dx = _controller.VelocityPixelsPerFrame.x;
                if (dx > 0.5f) ApplyFacing(1);
                if (dx < -0.5f) ApplyFacing(-1);
                return;
            }

            // `if(aiState > 0 && !(aiState == 3 && this.attState == 2))`.
            bool skipsTurn = _aiState == 3 && _attState == 2;
            if (skipsTurn)
            {
                return;
            }

            // `aiNapr = celX > X ? 1 : -1;` (:422). Read from the aim point, not from the live target:
            // a boss with no sighting keeps turning toward the stale point, which is what the oracle
            // does.
            Vector2 selfAs3 = ToAs3(_controller.transform.position);
            int wanted = _celAs3.x > selfAs3.x ? 1 : -1;

            BossUltraRules.TickFacing(
                _controller.FacingDirection, wanted, _tTurn, skipsTurn, out int facing, out int turnTimer);

            _tTurn = turnTimer;
            if (facing != _controller.FacingDirection)
            {
                ApplyFacing(facing);
            }
        }

        // ── the attack (:457-498) ────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>attack()</c> — the four weapon arms plus the body check, in the oracle's order.
        /// </summary>
        void Attack()
        {
            // `if(sost != 1) return;` (:459-462) — a corpse does not attack. The caller already gated on
            // IsAlive, so this is the oracle's line restated rather than a second test.
            if (!_controller.IsAlive)
            {
                return;
            }

            if (_aiState == 1)
            {
                // `if(aiState == 1 && Boolean(celUnit)) attKorp(celUnit, 1);` (:466-469) — the sentinel
                // body-checks whatever it flies through at FULL damage. `TryContactAttack` is the port
                // of `attKorp`: a collision-box AABB test, not a distance threshold, and it refuses a
                // target already inside its contact window.
                _controller.TryContactAttack(_blackboard.TargetUnit, 1f);
            }
            else if (_aiState == 3)
            {
                if (_attState == 0)
                {
                    FireOnce(WeaponRockets);
                }
                else if (_attState == 1)
                {
                    FireOnce(_usil ? WeaponGatlingEnhanced : WeaponGatling);
                }
                else if (_attState == 4)
                {
                    FireOnce(WeaponGas);
                }
                else
                {
                    // `this.thWeapon.forceRot += 0.1; this.thWeapon.attack();` (:488-492). The angle
                    // advances BEFORE the shot, so the first shell of a volley already leaves at 0.1 rad
                    // rather than straight ahead.
                    _forceRot += GrenadeRotationPerTick;
                    FireOnce(WeaponGrenades);
                }

                // `if(dist2 < 100*100 && isrnd(0.1)) attKorp(celUnit, 0.5);` (:493-496).
                //
                // **`dist2` is a dead field.** It is declared `public var dist2:Number = 0` on
                // Obj.as:48 and is never written anywhere in the 1016 oracle files (only these two boss
                // attacks read it), so the comparison is `0 < 10000` — always true. The gate is
                // therefore the roll alone, and the real filter is `attKorp`'s AABB test, which the
                // port's TryContactAttack performs. Reproducing the always-true term would be theatre;
                // the behaviour is "10 % per tick while the boxes overlap".
                if (UnityEngine.Random.value < BossUltraRules.ContactAttackChance)
                {
                    _controller.TryContactAttack(_blackboard.TargetUnit, 0.5f);
                }
            }
        }

        /// <summary>
        /// <c>weapon.attack()</c> — a <b>call</b>, not a burst. The weapon's own <c>t_attack</c> paces
        /// the rounds, so the brain asks every tick of step 3 and the mount decides.
        /// </summary>
        void FireOnce(string weaponId)
        {
            Enemy?.GetWeaponMount(weaponId)?.FireOnce();
        }

        // ── the shield (:448-455 and :438-445) ───────────────────────────────────────────────────

        /// <summary>
        /// <c>castShit()</c> — refill the pool on a reposition, on the difficulties the oracle names.
        /// </summary>
        /// <remarks>
        /// <b>On difficulties 0-2 this never fires</b>, so the 2000-point phase-two pool is the only
        /// shield the boss ever has there. On Very Hard it refills 500 points every reposition, which is
        /// what makes the fight a damage race rather than a war of attrition.
        /// </remarks>
        void CastShield()
        {
            if (!BossUltraRules.ShouldCastShield(ShieldHp, _tShit, GlobalDifficulty.Value, CurrentHpFraction))
            {
                return;
            }

            Ultra?.SetShieldHp(Ultra.ShieldMaxHp);
            _tShit = BossUltraRules.ShieldRechargeTimer;
        }

        /// <summary>
        /// The phase change at half HP (<c>:438-445</c>): a 2000-point pool, and the upgraded gatling
        /// swapped in.
        /// </summary>
        /// <remarks>
        /// <b>It overwrites the pool rather than adding to it.</b> <c>shithp = shitMaxHp * 4</c> is an
        /// assignment, so a boss that enters phase two with 480 points of shield left ends up with 2000,
        /// not 2480. And <c>t_shit = 2000</c> is a <i>lockout</i>: <c>castShit</c> cannot top it up again
        /// for 2000 ticks (66 s), so the phase-two shield does not regenerate.
        /// </remarks>
        void EnterEnhancedPhase()
        {
            _usil = true;
            _tShit = BossUltraRules.PhaseShieldTimer;

            Ultra?.EnterEnhancedPhase();

            // `currentWeapon.vis.visible = false; currentWeapon2.vis.visible = true;` (:443-444). The
            // gatling is replaced, not upgraded in place: both mounts exist from the spawn, and the swap
            // is a visibility flip. `robogatp2` is built hidden — see BossUltraController.EquipBossWeapons.
            Enemy?.GetWeaponMount(WeaponGatling)?.SetHeldVisualVisible(false);
            Enemy?.GetWeaponMount(WeaponGatlingEnhanced)?.SetHeldVisualVisible(true);

            SetPhase(2);
        }

        /// <summary>
        /// <c>vulner[Unit.D_EMP] = shithp &gt; 0 ? 0.2 : 1</c> (<c>:284</c>) — the only vulnerability this
        /// class ever writes, and the reason the shield is worth shooting: while it is up, EMP does a
        /// fifth of its damage.
        /// </summary>
        /// <remarks>
        /// <b>The baseline comes from the unit's own row, not from a neutral table.</b>
        /// <c>bossultra</c> authors <c>&lt;vulner emp='1' fire='0.5' cryo='0.5' venom='0' poison='0'
        /// bleed='0'/&gt;</c> (<c>AllData.as:1569</c>), and rebuilding the table from a default would
        /// silently discard the fire/cryo/venom/poison/bleed rows. The EMP entry is the one this class
        /// owns; everything else is the data's.
        ///
        /// <para><b>Written only when it changes.</b> AS3 assigns every frame, which is free on an array.
        /// Here each write rebuilds and re-folds the whole table, so the assignment is gated on the value
        /// actually differing — same observable result, one comparison instead of a table rebuild per
        /// tick.</para>
        /// </remarks>
        void SyncEmpVulnerability()
        {
            UnitStats stats = _controller.UnitStats;
            if (stats == null) return;

            float wanted = BossUltraRules.EmpVulnerability(ShieldHp);
            if (!float.IsNaN(_empVulnerabilityWritten) &&
                Mathf.Approximately(_empVulnerabilityWritten, wanted))
            {
                return;
            }

            VulnerabilityData table = _controller.Definition != null
                ? _controller.Definition.vulnerabilities
                : VulnerabilityData.Neutral;
            table.SetVulnerability(DamageType.EMP, wanted);

            stats.SetVulnerabilityBaseline(table);
            _empVulnerabilityWritten = wanted;
        }

        // ── aiming ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>setCel(loc.gg)</c> — aim at the player's torso.
        /// </summary>
        /// <remarks>
        /// <para><b>The point is the torso, not the feet.</b> <c>setCel</c> writes
        /// <c>celX = param1.X + param1.scX/4 * param1.storona</c> and
        /// <c>celY = param1.Y - param1.scY/2</c> (<c>Unit.as:4707-4708</c>) — a quarter body forward and
        /// half a body up. Aiming at the feet instead would drop every rocket volley a body-length short,
        /// and it would also move the <c>celY &gt; 16*40</c> test that decides the grenade beat.</para>
        ///
        /// <para><b>Deviation, recorded.</b> AS3's <c>loc.gg</c> is a global handle on the player, so the
        /// boss aims at him whether or not it can see him. The port has no player global; this uses the
        /// visible target and then the last known position — the same substitution
        /// <c>BossAlicornBrain.Aim</c> documents. In a locked boss room with
        /// <c>overlook='1'</c> (see <see cref="ApplyTuning"/>) the two agree; behind cover they do not,
        /// and the port's boss loses the aim for as long as the sighting does.</para>
        /// </remarks>
        void Aim()
        {
            UnitController target = _blackboard.TargetUnit;
            if (target != null)
            {
                Vector2 feetAs3 = ToAs3(target.transform.position);
                Vector2 size = target.SpriteSizePixels;

                _celAs3 = new Vector2(
                    feetAs3.x + size.x * 0.25f * target.FacingDirection,
                    feetAs3.y - size.y * 0.5f);
                _hasCel = true;
                return;
            }

            if (_blackboard.HasLastKnownTargetPosition)
            {
                Vector2 lastAs3 = ToAs3(_blackboard.LastKnownTargetPosition);
                _celAs3 = lastAs3;
                _hasCel = true;
            }
        }

        /// <summary>
        /// Push <see cref="_celAs3"/> onto every mount, then override the grenade launcher with its own
        /// <c>forceRot</c> angle.
        /// </summary>
        /// <remarks>
        /// <b>The override is not a special case, it is <c>findCel = false</c>.</b> Every other weapon
        /// takes its angle from <c>celX/celY</c> (<c>Weapon.as:1068-1086</c>); <c>thWeapon</c> was built
        /// with <c>findCel = false</c> (<c>UnitBossUltra.as:79</c>), which sends it down the
        /// <c>rot2 = this.forceRot</c> branch instead. The port has no per-weapon <c>findCel</c> flag, so
        /// the same effect is achieved by pointing that one mount at a synthetic aim point one unit along
        /// the angle.
        /// </remarks>
        void PublishAim()
        {
            EnemyController enemy = Enemy;
            if (enemy == null) return;

            if (_hasCel)
            {
                enemy.SetWeaponAimPoint(ToWorld(_celAs3));
            }

            EnemyWeaponMount grenades = enemy.GetWeaponMount(WeaponGrenades);
            if (grenades == null) return;

            // AS3 `rot` is measured with y down, so the port's direction is `(cos, -sin)`.
            Vector2 direction = new Vector2(Mathf.Cos(_forceRot), -Mathf.Sin(_forceRot));
            grenades.SetAimTarget(grenades.ResolveHoldPoint() + direction);
        }

        // ── the death sequence (:193-220, :151-154) ─────────────────────────────────────────────

        /// <summary>
        /// <c>animate()</c>'s <c>sost == 2</c> block — the 150-tick death, exploding harder as it goes.
        /// </summary>
        /// <remarks>
        /// <para><b>The chance ramps.</b> <c>isrnd(0.3 - timerDie/500)</c> with <c>timerDie</c> counting
        /// <i>down</i> from 150 (<c>Unit.as:1792</c>) gives 0 at the first tick and 0.3 at the last, so the
        /// sentinel sits almost still for half a second and then comes apart. Running the counter upward
        /// and inverting it, as here, is the same curve.</para>
        ///
        /// <para><b>Each burst is three things at once</b> — an <c>expl</c> particle at a random point
        /// within ±60 px, a <c>metal</c> debris part, and the <c>expl_e</c> sound.</para>
        /// </remarks>
        void TickDeathThroes()
        {
            if (_deathThroes <= 0)
            {
                _deathThroes = BossUltraRules.DeathTimerTicks;
            }

            _deathThroes--;
            if (_deathThroes < 0)
            {
                return;
            }

            float chance = 0.3f - _deathThroes / 500f;
            if (chance <= 0f || UnityEngine.Random.value >= chance)
            {
                return;
            }

            Vector2 selfWorldPixels = ToWorldPixels(_controller.transform.position);
            Vector2 burst = selfWorldPixels + new Vector2(
                UnityEngine.Random.value * 120f - 60f,
                // `Y - Math.random()*120` in AS3's y-down space is upward, so the port adds.
                UnityEngine.Random.value * 120f);

            _controller.EmitParticle("expl", burst);

            // `newPart("metal")` — one debris part, not twenty-two; the 22 belong to `expl()` and are
            // emitted once by the controller's final-death hook.
            _controller.EmitParticle("metal", burst);

            Enemy?.Sound?.Play("expl_e", transform.position);
        }

        /// <summary>
        /// AS3 <c>animate()</c> (<c>:193-220</c>): the grenade launcher's held sprite is hidden every
        /// frame, and the dome follows <c>shithp</c>.
        /// </summary>
        /// <remarks>
        /// <b><c>thWeapon.vis.visible = false</c> is unconditional</b> — the grenade launcher is never
        /// drawn, not even while it fires. It is a hull-mounted pod the sprite sheet has no frame for.
        /// </remarks>
        protected override string ResolveAnimState()
        {
            if (_controller == null) return null;
            if (!_controller.IsAlive) return "die";

            Enemy?.GetWeaponMount(WeaponGrenades)?.SetHeldVisualVisible(false);

            return "stay";
        }

        // ── coordinate conversion ────────────────────────────────────────────────────────────────

        /// <summary>
        /// A Unity world position as AS3 room-local pixels — x from the left wall, <b>y downward from the
        /// ceiling</b>.
        /// </summary>
        /// <remarks>
        /// Outside a room (a bare fixture spawn) there is no origin and no room height to mirror
        /// against, so this degrades to a plain pixel conversion. That keeps the offline fixtures
        /// runnable; nothing in the fight is correct without a room, and nothing in the fight runs
        /// without one.
        /// </remarks>
        protected Vector2 ToAs3(Vector3 worldPosition)
        {
            if (_tileQuery == null)
            {
                return new Vector2(worldPosition.x, worldPosition.y) * TileQueryConstants.UnitToPixel;
            }

            return WorldCoordinates.UnityWorldToAs3RoomLocal(
                worldPosition, OriginPixel, RoomHeightTiles);
        }

        /// <summary>The inverse of <see cref="ToAs3"/> — a Unity world position in <b>units</b>.</summary>
        protected Vector3 ToWorld(Vector2 as3Local)
        {
            if (_tileQuery == null)
            {
                return (Vector3)(as3Local * TileQueryConstants.PixelToUnit);
            }

            return WorldCoordinates.As3RoomLocalToUnityWorld(as3Local, OriginPixel, RoomHeightTiles);
        }

        /// <summary>
        /// A Unity world position in <b>world pixels</b> — the space <c>EmitParticle</c> takes, which is
        /// up-positive and therefore needs no mirror.
        /// </summary>
        protected Vector2 ToWorldPixels(Vector3 worldPosition)
        {
            return new Vector2(worldPosition.x, worldPosition.y) * TileQueryConstants.UnitToPixel;
        }

        /// <summary>Emit a particle at an AS3 room-local point.</summary>
        void EmitAtAs3(string particleId, Vector2 as3Local)
        {
            Vector3 world = ToWorld(as3Local);
            _controller.EmitParticle(particleId, ToWorldPixels(world));
        }

        /// <summary>
        /// AS3's <c>UnitBrain</c> Attack state does not exist for this boss — see
        /// <see cref="Attack"/>, which is called from step 3 of the rotation.
        /// </summary>
        /// <remarks>
        /// <see cref="EnemyBrain"/> declares this abstract because the family brains reach their attack
        /// through the shared Attack state. This boss does not: <see cref="SimTick"/> never enters the
        /// base machine, so <c>TryEnterAttackState</c> is never reached and there is no path that could
        /// call this. An empty body is therefore honest rather than a stub; putting the real attack here
        /// would give the boss a second firing path on top of the rotation.
        /// </remarks>
        protected override void ExecuteAttackAction()
        {
        }

        /// <summary><c>animate():230</c> — the sentinel never stops to attack; its locomotion is
        /// <see cref="ApplyFlight"/>, not the base chase.</summary>
        protected override bool StopsToAttack => false;
    }
}
