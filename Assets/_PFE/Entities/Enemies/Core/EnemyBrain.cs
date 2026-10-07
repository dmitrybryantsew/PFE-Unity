using System;
using System.Collections.Generic;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Physics;
using UnityEngine;

namespace PFE.Entities.Enemies
{
    /// <summary>
    /// Base behavioral brain for enemy AI.
    /// Runs on the fixed 30 Hz simulation clock (<see cref="ISimTickable"/>) at <see cref="SimTickOrder.UnitMotor"/> - 1.
    /// Gathers perception via <see cref="EnemySensors"/> and commands movement via <see cref="IMovementMotor"/>.
    /// </summary>
    public abstract class EnemyBrain : MonoBehaviour, ISimTickable, IEnemyBrain
    {
        [Header("Brain Settings")]
        [SerializeField] protected float _patrolSpeed = 2f;
        [SerializeField] protected float _chaseSpeed = 4f;
        /// <summary>
        /// AS3's <c>aiState == 2</c> speed. Every unit that models it scales its own run speed —
        /// <c>UnitZombie.as:709</c> is <c>maxSpeed = runSpeed * 0.6</c> — rather than using the walk
        /// speed, because state 2 is "heard something, closing on it", not "wandering".
        /// </summary>
        [SerializeField] protected float _alertSpeed = 2f;
        [SerializeField] protected float _attackRangePixels = 45f;
        [SerializeField] protected int _attackCooldownTicks = 30; // 1 second at 30 fps
        [SerializeField] protected int _patrolDurationTicks = 90; // 3 seconds
        [SerializeField] protected int _idleDurationTicks = 60;   // 2 seconds

        /// <summary>
        /// How long the unit spends climbing out of the floor — AS3 <c>UnitZombie</c>'s
        /// <c>aiTCh = 24</c>, set both by <c>alarma()</c> when it promotes a buried unit
        /// (<c>UnitZombie.as:337-341</c>) and by <c>control()</c> when the target walks into the probe
        /// cell (<c>:595-601</c>). At the oracle's 30 Hz that is 0.8 s.
        ///
        /// <para>Named here rather than inlined in <c>ZombieBrain</c> because it is the length of the
        /// <see cref="EnemyAIState.Digging"/> state, and that state is driven by the base
        /// <see cref="SetState"/> timer machinery like every other one.</para>
        /// </summary>
        [SerializeField] protected int _digDurationTicks = 24;

        protected EnemyAIState _currentState = EnemyAIState.Idle;
        protected readonly EnemyBlackboard _blackboard = new EnemyBlackboard();
        protected readonly EnemySensors _sensors = new EnemySensors();

        protected UnitController _controller;
        protected IMovementMotor _motor;
        protected ITileQueryService _tileQuery;
        protected SimLoop _simLoop;

        /// <summary>
        /// The sheet animator on this unit's <c>Visual</c> child, if it has one. Resolved here rather
        /// than fetched per tick, and nullable on purpose: a bare test spawn has no renderer.
        /// </summary>
        protected UnitAnimator _animator;

        // Candidate cache to avoid per-tick allocation
        protected readonly List<UnitController> _targetCandidates = new List<UnitController>(4);

        public EnemyAIState CurrentState => _currentState;
        public EnemyBlackboard Blackboard => _blackboard;
        public EnemySensors Sensors => _sensors;

        /// <summary>
        /// The live targets this brain evaluated this tick, exposed read-only so the debug overlay can
        /// ask the same question the sensor asked.
        ///
        /// <para><b>Why the overlay needs this and cannot guess.</b> Since the hearing radius is
        /// <c>noise × ear × earMult</c> rather than a constant, there is no longer a single number to
        /// draw — the circle's size depends on how loud the thing being listened for is. Drawing a
        /// fixed circle would be drawing the old bug. So the overlay reads the candidates the sensor
        /// actually used and takes the loudest one's noise, which is exactly what decides whether
        /// anything is audible at all.</para>
        /// </summary>
        public IReadOnlyList<UnitController> TargetCandidates => _targetCandidates;

        /// <summary>
        /// The sensor origin, projected from <see cref="GetEyePositionPixels"/>.
        ///
        /// <para><b>One expression, two readers.</b> The brain's own tick casts from this, and so does
        /// the debug overlay. Writing the arithmetic a second time in the overlay would make the drawn
        /// vision cone a claim about the overlay rather than about the sensor — and the day the two
        /// drifted, the picture would look authoritative and be wrong.</para>
        /// </summary>
        public Vector2 EyePositionPixels => GetEyePositionPixels();

        /// <summary>
        /// Runs at order 24, immediately before <see cref="SimTickOrder.UnitMotor"/> (25).
        /// Ensures motor intent is enacted in the exact same simulation tick.
        /// </summary>
        public virtual int TickOrder => SimTickOrder.UnitMotor - 1;

        protected virtual void Awake()
        {
            ResolveComponents();
        }

        /// <summary>
        /// Resolve the sibling components this brain drives — the unit controller, the movement motor and
        /// the sheet animator. Idempotent, and safe to call before any of them exists.
        ///
        /// <para><b>Why this is a method called from three places rather than three lines in
        /// <see cref="Awake"/>.</b> <c>Awake</c> is the wrong place to depend on a sibling existing, and
        /// that is documented engine behaviour rather than a style preference: the manual for
        /// <c>MonoBehaviour.Awake</c> states the order Unity calls components' <c>Awake</c> is not
        /// deterministic and warns that you must not assume "a reference set up by one GameObject's
        /// Awake will be usable in another GameObject's Awake", and <c>RequireComponent</c>'s page says a
        /// required component is added "as a dependency" when <c>AddComponent</c> is called.</para>
        ///
        /// <para><b>The bug this fixes, because it cost a play-test.</b> <c>ZombieController</c> is
        /// decorated <c>[RequireComponent(typeof(ZombieBrain))]</c>, so
        /// <c>AddComponent&lt;ZombieController&gt;()</c> creates the <b>brain first</b> and runs its
        /// <c>Awake</c> before the controller exists. The brain's
        /// <c>GetComponent&lt;UnitController&gt;()</c> therefore returned <c>null</c>, and nothing ever
        /// retried it, so <c>_controller</c> stayed null for the unit's entire life. <b>Two visible
        /// symptoms, one cause:</b> <see cref="ZombieBrain.ResolveAnimState"/> early-returns <c>null</c>
        /// when <c>_controller</c> is null, so nothing ever drove the animator and the zombie showed no
        /// walk or run; and <c>ZombieBrain.ExecuteAttackAction</c> dereferenced it, so every attack
        /// attempt threw <c>NullReferenceException</c> out of the sim loop.</para>
        ///
        /// <para><c>_motor</c> was unaffected — <c>RoomUnitSpawner</c> adds the motor <i>before</i> the
        /// controller — which is exactly why the zombie could still move around while displaying no
        /// animation at all. The two symptoms looked like two bugs and were one.</para>
        /// </summary>
        protected void ResolveComponents()
        {
            if (_controller == null) _controller = GetComponent<UnitController>();
            if (_motor == null) _motor = GetComponent<IMovementMotor>();

            // Include inactive children: RoomUnitSpawner builds the "Visual" child and adds the animator
            // to it before this controller exists, but a future spawner that deactivates the visual while
            // it warms up would otherwise leave this null and silently kill the animation.
            if (_animator == null) _animator = GetComponentInChildren<UnitAnimator>(true);

            SyncSensorTuning();
        }

        /// <summary>
        /// Push this unit's own sensory numbers from its definition into <see cref="_sensors"/>.
        ///
        /// <para><b>Without this the sensors are a deaf-and-blind default for everyone.</b>
        /// <see cref="EnemySensors.Ear"/> and <see cref="EnemySensors.ObservationPower"/> are per-unit
        /// data — <c>ear='0'</c> is authored by every small robot in the roster and <c>obs='6'</c> by
        /// <c>zombie9</c> — and they are the listener's half of both perception formulas. They were
        /// never read from the unit, so every enemy heard with the same ears and noticed at the same
        /// rate; that is the same failure shape as the constant hearing radius, one layer down.</para>
        ///
        /// <para>Called from two places because the two are ordered by Unity rather than by this code:
        /// <see cref="ResolveComponents"/> runs at <c>Awake</c>, when <c>Stats</c> may still be null, and
        /// <see cref="OnDefinitionAssigned"/> runs the moment it is not. Calling it twice is free and
        /// calling it once at the wrong moment is a silent default.</para>
        /// </summary>
        protected void SyncSensorTuning()
        {
            if (_controller == null)
            {
                return;
            }

            _sensors.Ear = _controller.Ear;
            _sensors.ObservationPower = _controller.ObservationPower;
        }

        /// <summary>
        /// Hand the unit controller over explicitly, then re-resolve everything else.
        ///
        /// <para><b>Called by <see cref="EnemyController.Awake"/>, which is the only place that knows
        /// the controller has just come into existence.</b> <see cref="ResolveComponents"/> would find it
        /// too, but only from its <i>next</i> call onwards — and by then the brain's own <c>Awake</c> has
        /// already run and returned. Handing it over here means <c>_controller</c> is correct before any
        /// <c>Initialize</c> call, so a brain reading the definition in
        /// <see cref="OnDefinitionAssigned"/> can never observe a half-built unit.</para>
        /// </summary>
        public void AttachController(UnitController controller)
        {
            _controller = controller;
            ResolveComponents();
            _blackboard.FacingDirection = _controller != null ? _controller.FacingDirection : 1;
        }

        protected virtual void OnDestroy()
        {
            if (_simLoop != null)
            {
                _simLoop.Unregister(this);
                _simLoop = null;
            }
        }

        /// <summary>
        /// Attaches simulation loop and tile query services from room spawner.
        /// </summary>
        public virtual void Initialize(ITileQueryService tileQuery, SimLoop simLoop)
        {
            _tileQuery = tileQuery;
            _simLoop = simLoop;

            // The brain is created by `[RequireComponent]` before the controller exists, so its `Awake`
            // could not see it. Re-resolve here: this is the last seam before the brain starts ticking,
            // and it is also the only one that runs in a venue where `Awake` does not (EditMode).
            ResolveComponents();

            if (_simLoop != null)
            {
                // Loud rather than silent. A brain with no controller cannot move, attack or animate, and
                // the previous failure mode was exactly this — a unit that stood there with no log line,
                // which reads as "the animator is broken" and costs a play-test to re-diagnose. Only
                // warned when the brain is actually about to be registered, so a bare test spawn stays
                // quiet.
                if (_controller == null)
                {
                    Debug.LogWarning(
                        $"[{GetType().Name}] no UnitController on '{name}': this brain will not move, " +
                        "attack or animate. Attach the controller first, or call AttachController.",
                        this);
                }

                _simLoop.Register(this);
            }

            _blackboard.FacingDirection = _controller != null ? _controller.FacingDirection : 1;
            SetState(EnemyAIState.Idle);
        }

        /// <summary>
        /// Called by <see cref="EnemyController"/> as soon as its <see cref="PFE.Data.Definitions.UnitDefinition"/>
        /// has been assigned — the moment a brain can read the unit's real numbers.
        ///
        /// <para><b>Why a hook and not just <see cref="Awake"/>.</b> <c>RoomUnitSpawner</c> adds the
        /// controller (which runs <c>Awake</c>) and only then calls
        /// <c>controller.Initialize(definition, stats)</c>, so at <c>Awake</c> time <c>Stats</c> is
        /// <c>null</c> and any data read there silently keeps the serialized placeholder. The
        /// alternative — reading lazily on every tick — would hide the same failure and cost a null
        /// check per tick forever.</para>
        /// </summary>
        public virtual void OnDefinitionAssigned()
        {
            // `Stats` is real from here on, so this is the first moment the sensor tuning can be read.
            SyncSensorTuning();
        }

        /// <summary>
        /// Called once with this unit's <see cref="UnitInstance"/> after the controller has applied the
        /// placement's own fields — AS3's <c>setPos</c>, the step <c>Location.createUnit</c> reaches
        /// through <c>putLoc</c> immediately after the <c>Unit</c> constructor returns.
        ///
        /// <para><b>Why the placement needs its own hook rather than riding on
        /// <see cref="OnDefinitionAssigned"/>.</b> They carry different things. The definition is shared
        /// by every instance of a unit id and arrives through
        /// <c>controller.Initialize(definition, stats)</c>; the placement is per-spawn — facing, the
        /// authored <c>&lt;obj&gt;</c> attributes, and the ambush tier rolled for this one zombie — and it
        /// arrives through <c>controller.ApplyPlacement(unit)</c>. The oracle keeps them apart in the same
        /// way: the constructor reads the shared row, <c>setPos</c> reads the placed node.</para>
        ///
        /// <para><b>Ordering is safe without a guard.</b> <c>RoomUnitSpawner.Spawn</c> runs
        /// synchronously and calls <c>ApplyPlacement</c> as its last act, so no sim tick can run between
        /// the brain registering with <see cref="SimLoop"/> and this call — a brain may enter a
        /// placement-derived state here without the sim observing a tick of the wrong one.</para>
        /// </summary>
        public virtual void OnPlacementApplied(UnitInstance placement)
        {
        }

        public virtual void SetState(EnemyAIState newState)
        {
            if (_currentState == newState && newState != EnemyAIState.Idle)
            {
                return;
            }

            EnemyAIState previousState = _currentState;
            _currentState = newState;

            switch (newState)
            {
                case EnemyAIState.Idle:
                    _blackboard.StateTimerTicks = _idleDurationTicks;
                    StopMovement();
                    ClearInvestigation(previousState);
                    break;

                case EnemyAIState.Patrol:
                    _blackboard.StateTimerTicks = _patrolDurationTicks;
                    ClearInvestigation(previousState);
                    break;

                case EnemyAIState.Alert:
                    _blackboard.StateTimerTicks = _patrolDurationTicks;
                    break;

                case EnemyAIState.CombatChase:
                    if (previousState == EnemyAIState.Idle || previousState == EnemyAIState.Patrol || previousState == EnemyAIState.Alert)
                    {
                        // AS3 `alarma()` sets `shok` as it promotes an unaware unit into the chase
                        // (`UnitZombie.as:344-346`), so "entered the chase from an unaware state" is the
                        // port's equivalent moment. 5..19 — see `StaggerMath.AlarmTicks`. This was 3..7.
                        _blackboard.ShockTimerTicks = StaggerMath.AlarmTicks(
                            UnityEngine.Random.Range(0, StaggerMath.AlarmRollRange));

                        // …and this is also the oracle's `budilo()` moment: a unit that has just acquired
                        // a target shouts to its neighbours. See Budilo for the radius and the ear gate.
                        Budilo();
                    }
                    break;

                case EnemyAIState.Attack:
                    _blackboard.AttackCooldownTicks = _attackCooldownTicks;
                    StopMovement();
                    break;

                case EnemyAIState.Buried:
                    // AS3 `zakop()` (UnitZombie.as:414-445) also sets `fixed = true` (no locomotion),
                    // `invis = true` (see the note in ZombieBrain — nothing in the port reads an
                    // invisibility flag), `scY = 0` and `Y1 = Y2` (the unit shrinks into its own floor
                    // line), and zeroes `stealthMult`. Only the movement half has a port equivalent, and
                    // it is the half the player can see.
                    StopMovement();
                    break;

                case EnemyAIState.Digging:
                    // AS3 sets `aiTCh` at the two points of entry, not in one place: `alarma()` uses 24
                    // (UnitZombie.as:340) and so does the `celUnit` branch of `control()` (:600).
                    _blackboard.StateTimerTicks = _digDurationTicks;
                    StopMovement();
                    break;

                case EnemyAIState.Dead:
                    StopMovement();
                    // Draw the corpse on the frame it dies, not on the next tick. The oracle selects the
                    // death row from `sost` inside the same `animate()` call that sees the unit die
                    // (UnitZombie.as:234-251), and a unit that has been unregistered from the sim — or
                    // one whose next tick is 33 ms away — would otherwise hold its last walking frame.
                    UpdateAnimation();
                    // AS3 `Unit.die()` calls `loc.budilo(X, Y - scY / 2, this.noiseDie)` with
                    // `noiseDie = 800` — a kill is the loudest thing in the game and pulls the room.
                    Budilo(NoiseMath.DeathAlarmRadius);
                    break;
            }
        }

        /// <summary>
        /// Drop a pending sound investigation when this unit gives up on one.
        ///
        /// <para><b>This is the second half of the "permanently Alert" bug.</b> The hearing flag used to
        /// be set every tick the target was in range and cleared only by physically arriving within
        /// 20 px, so a unit that could not reach the position — one behind a wall, one on a ledge —
        /// stayed in <see cref="EnemyAIState.Alert"/> forever. In the oracle the investigation lives in
        /// <c>celX</c>/<c>celY</c>, and <c>celUnit</c> is nulled whenever the unit is not in state 2 or
        /// 3; leaving those states <i>is</i> the reset. So the clear happens on the transition out of
        /// Alert/Chase, not on a timer.</para>
        ///
        /// <para>Deliberately not cleared on every entry to Idle: <c>SetState(Idle)</c> is also how a
        /// unit re-enters the state it is already in (the guard at the top of <see cref="SetState"/>
        /// exempts Idle), and clearing there would drop a sound heard on the same tick.</para>
        /// </summary>
        private void ClearInvestigation(EnemyAIState previousState)
        {
            if (previousState == EnemyAIState.Alert || previousState == EnemyAIState.CombatChase)
            {
                _blackboard.HasHeardNoise = false;
            }
        }

        /// <summary>
        /// AS3 <c>Unit.budilo(param1 = 500)</c> — shout to every unit on the same side, then let each of
        /// them decide whether it heard.
        ///
        /// <para><b>Three rules, and the port had none of them.</b> The oracle's loop
        /// (<c>actionscript_project_context.txt:126071-126098</c>) walks <c>loc.units</c>, keeps only
        /// same-faction living units, and reaches a neighbour when
        /// <c>dist² &lt; radius² × neighbour.ear²</c> — the <b>receiver's</b> ear, not the shouter's. So
        /// a deaf ally never receives an alarm. Robots are the one exception: the <c>opt.robot</c> branch
        /// uses a plain <c>dist² &lt; radius²</c> with no ear factor, which is the only thing that makes
        /// the robot network reachable at all, because every robot authors <c>ear='0'</c>. The position
        /// handed over is randomised by <c>±125 px</c>, never exact.</para>
        ///
        /// <para><b>The shout is itself a sound.</b> <c>makeNoise(noiseRun * 1.2)</c> is the first line of
        /// the oracle's <c>budilo</c>, so shouting makes the shouter louder than running — which is what
        /// lets a player who survives one guard's alarm still be hunted by the room.</para>
        ///
        /// <para><b>Cost, and why it is acceptable here.</b> This is event-driven — on acquiring a target
        /// and on dying — not per tick, and it reuses the same <c>FindObjectsByType</c> the per-tick
        /// candidate refresh already performs. A faction registry would be cheaper and is the right
        /// follow-up; inventing one here would be a bigger change than the feature.</para>
        /// </summary>
        /// <param name="radius">
        /// AS3 <c>param1</c> — 500 by default (<c>:126071</c>), or <c>noiseDie = 800</c> on death
        /// (<c>:29075</c>, <c>:29836</c>).
        /// </param>
        protected virtual void Budilo(float radius = NoiseMath.DefaultAlarmRadius)
        {
            if (_controller == null || radius <= 0f)
            {
                return;
            }

            // AS3's first line: `this.makeNoise(this.noiseRun * 1.2);`
            _controller.MakeNoise(
                Mathf.RoundToInt(_controller.NoiseRun * AlarmNoiseMultiplier), isEvent: true);

            FactionType faction = _controller.Faction;
            Vector2 selfPx = (Vector2)_controller.transform.position * TileQueryConstants.UnitToPixel;

            var brains = FindObjectsByType<EnemyBrain>(FindObjectsSortMode.None);
            for (int i = 0; i < brains.Length; i++)
            {
                EnemyBrain neighbour = brains[i];
                if (neighbour == null || neighbour == this || neighbour._controller == null)
                {
                    continue;
                }

                if (!neighbour._controller.IsAlive || neighbour._controller.Faction != faction)
                {
                    continue;
                }

                Vector2 otherPx =
                    (Vector2)neighbour._controller.transform.position * TileQueryConstants.UnitToPixel;
                float distSq = (otherPx - selfPx).sqrMagnitude;

                if (!NoiseMath.AlarmReaches(
                        distSq,
                        radius,
                        neighbour._controller.Ear,
                        // NOT the neighbour's `EarMultiplier`, and that is deliberate: `Unit.budilo`'s
                        // gate is `dist² < radius² × _loc2_.ear²` (`:126090`) with **no `earMult` term at
                        // all**. The location-level `Location.budilo` is the variant that folds `earMult`
                        // in (`:36411`) — and it is a different function with different gates (no faction
                        // test), which this port does not implement. Passing the field here would apply a
                        // difficulty modifier twice over once the halving is ever wired, and today it
                        // would be a no-op that reads like fidelity.
                        NoiseMath.DefaultEarMultiplier,
                        neighbour._controller.IsMechanical))
                {
                    continue;
                }

                Vector2 jitter = NoiseMath.RandomisedOffset(
                    NoiseMath.UnitAlarmSpreadPixels,
                    UnityEngine.Random.value,
                    UnityEngine.Random.value);

                neighbour._sensors.RaiseAlarm(
                    neighbour._blackboard, selfPx + jitter, durationTicks: _patrolDurationTicks);
            }
        }

        /// <summary>
        /// AS3 <c>Unit.budilo()</c>'s <c>this.noiseRun * 1.2</c> — a shout is 20 % louder than a sprint,
        /// so the alarm propagates as sound as well as by the loop above.
        /// </summary>
        protected const float AlarmNoiseMultiplier = 1.2f;

        public virtual void OnDamaged(float amount, Vector2 damageSourcePx)
        {
            if (_currentState == EnemyAIState.Dead)
            {
                return;
            }

            _sensors.RaiseAlarm(_blackboard, damageSourcePx, durationTicks: 90);

            if (_currentState == EnemyAIState.Idle || _currentState == EnemyAIState.Patrol)
            {
                SetState(EnemyAIState.Alert);
            }
        }

        public virtual void SimTick(int tickIndex)
        {
            if (_currentState == EnemyAIState.Dead)
            {
                // A corpse still animates. The oracle branches on `sost` <i>first</i>
                // (`UnitZombie.as:234-251`), before it looks at `aiState` or `dx`, and the death
                // sequence is three separate rows — `die`, then `death` while airborne, then `fall` once
                // the body lands. Returning without a draw freezes the corpse on whatever walking frame
                // it died on, which is the visible half of "the zombie has no animation".
                UpdateAnimation();
                return;
            }

            // Decrement timers
            if (_blackboard.ShockTimerTicks > 0) _blackboard.ShockTimerTicks--;
            if (_blackboard.AttackCooldownTicks > 0) _blackboard.AttackCooldownTicks--;
            if (_blackboard.AlertTimerTicks > 0) _blackboard.AlertTimerTicks--;

            // Update candidate targets (find living players)
            RefreshTargetCandidates();

            // Evaluate sensory perception
            Vector2 eyePosPx = GetEyePositionPixels();
            _sensors.Evaluate(_blackboard, eyePosPx, _blackboard.FacingDirection, _tileQuery, _targetCandidates, tickIndex);

            // Execute state tick
            switch (_currentState)
            {
                case EnemyAIState.Idle:
                    TickIdle(tickIndex);
                    break;

                case EnemyAIState.Patrol:
                    TickPatrol(tickIndex);
                    break;

                case EnemyAIState.Alert:
                    TickAlert(tickIndex);
                    break;

                case EnemyAIState.CombatChase:
                    TickCombatChase(tickIndex);
                    break;

                case EnemyAIState.Attack:
                    TickAttack(tickIndex);
                    break;

                case EnemyAIState.Buried:
                    TickBuried(tickIndex);
                    break;

                case EnemyAIState.Digging:
                    TickDigging(tickIndex);
                    break;
            }

            // ...and draw, which the oracle also does last. `UnitZombie.animate()` runs after
            // `control()` in the same frame, and the state it picks is a function of the decision
            // `control()` just made — including the speed this tick commanded.
            UpdateAnimation();
        }

        /// <summary>
        /// The AS3 <c>animState</c> id this unit should be drawing, or <c>null</c> for "no opinion".
        ///
        /// <para><b>The base returns <c>null</c> on purpose, and that is not a stub.</b> In the oracle
        /// <c>Unit.animate()</c> is <i>empty</i> (<c>Unit.as:2936-2938</c>) and every concrete unit
        /// overrides it with its own state selection — 40 files, one implementation each, and they do not
        /// agree with one another. There is no shared shape to hoist, so inventing a generic
        /// walk/trot/run mapping here would be porting behaviour the oracle does not have, and it would
        /// be indistinguishable from a real port. Each archetype opts in by overriding this and citing
        /// the <c>animate()</c> it ported.</para>
        /// </summary>
        protected virtual string ResolveAnimState()
        {
            return null;
        }

        /// <summary>
        /// Push <see cref="ResolveAnimState"/>'s answer into the animator. Re-selecting the state already
        /// showing is a no-op inside <see cref="UnitAnimator.SetState"/>, which is what lets an AI assert
        /// its state every tick without rewinding the animation.
        /// </summary>
        protected virtual void UpdateAnimation()
        {
            if (_animator == null)
            {
                return;
            }

            string state = ResolveAnimState();
            if (!string.IsNullOrEmpty(state))
            {
                _animator.SetState(state);
            }
        }

        protected virtual void TickIdle(int tickIndex)
        {
            if (_blackboard.TargetUnit != null && _blackboard.HasLineOfSight)
            {
                SetState(EnemyAIState.CombatChase);
                return;
            }

            if (_blackboard.HasHeardNoise)
            {
                SetState(EnemyAIState.Alert);
                return;
            }

            if (--_blackboard.StateTimerTicks <= 0)
            {
                // Turn around and patrol
                _blackboard.FacingDirection = -_blackboard.FacingDirection;
                ApplyFacing(_blackboard.FacingDirection);
                SetState(EnemyAIState.Patrol);
            }
        }

        protected virtual void TickPatrol(int tickIndex)
        {
            if (_blackboard.TargetUnit != null && _blackboard.HasLineOfSight)
            {
                SetState(EnemyAIState.CombatChase);
                return;
            }

            if (_blackboard.HasHeardNoise)
            {
                SetState(EnemyAIState.Alert);
                return;
            }

            MoveHorizontal(_blackboard.FacingDirection * _patrolSpeed);

            if (--_blackboard.StateTimerTicks <= 0)
            {
                SetState(EnemyAIState.Idle);
            }
        }

        protected virtual void TickAlert(int tickIndex)
        {
            if (_blackboard.TargetUnit != null && _blackboard.HasLineOfSight)
            {
                SetState(EnemyAIState.CombatChase);
                return;
            }

            // Move toward the committed sound, or failing that the last place the target was seen.
            //
            // Both branches are gated on their own "has ever been set" flag rather than on the position
            // being non-zero. `LastKnownTargetPosition` is a Vector2 and (0, 0) is a real place — the
            // corner of the map — so reading it unconditionally sent a unit that had only ever *heard*
            // something on a long walk to the world origin the moment it arrived and the heard latch was
            // cleared. That path is reachable now and was not before: the latch used to be re-armed
            // every tick while the target was in range, so the fallback was almost never taken.
            Vector2 goalPx;
            bool hasGoal;

            if (_blackboard.HasHeardNoise)
            {
                goalPx = _blackboard.LastHeardNoisePosition;
                hasGoal = true;
            }
            else if (_blackboard.HasLastKnownTargetPosition)
            {
                goalPx = _blackboard.LastKnownTargetPosition;
                hasGoal = true;
            }
            else
            {
                goalPx = Vector2.zero;
                hasGoal = false;
            }

            float currentX = transform.position.x * 100f;
            float diffX = goalPx.x - currentX;

            if (hasGoal && Mathf.Abs(diffX) > 20f)
            {
                int moveDir = diffX > 0 ? 1 : -1;
                _blackboard.FacingDirection = moveDir;
                ApplyFacing(moveDir);
                MoveHorizontal(moveDir * _alertSpeed);
            }
            else
            {
                _blackboard.HasHeardNoise = false;
                StopMovement();
            }

            if (_blackboard.AlertTimerTicks <= 0 && --_blackboard.StateTimerTicks <= 0)
            {
                SetState(EnemyAIState.Idle);
            }
        }

        protected virtual void TickCombatChase(int tickIndex)
        {
            if (_blackboard.TargetUnit == null || !_blackboard.TargetUnit.IsAlive)
            {
                _blackboard.ClearTarget();
                SetState(EnemyAIState.Alert);
                return;
            }

            // Target lost for more than 3 seconds (90 ticks)
            if (_blackboard.TimeSinceTargetSpottedTicks > 90)
            {
                SetState(EnemyAIState.Alert);
                return;
            }

            // ── Port-only reaction pause. This is NOT an AS3 `shok` behaviour. ──────────────────────
            //
            // This gate was previously commented "Reaction pause (AS3: shok)". That attribution was
            // wrong twice over, and the correction matters because it changes how the value should be
            // read:
            //
            //   * In the oracle `shok` never gates movement. Its only two effects are halving a
            //     zombie's contact damage (`UnitZombie.as:940`) and refusing a raider's attacks
            //     (`UnitRaider.as:1486`, `:1498`, `:1521`) — both offence, neither locomotion. The
            //     damage half is already modelled (`ZombieBrain.ExecuteAttackAction`, via
            //     `StaggerMath.HalvesOutgoingDamage`).
            //   * The pause this code actually wants is a DIFFERENT field: `aiSpok` (`maxSpok = 30`,
            //     `Unit.as:362`), which the oracle counts up to `maxSpok + 10` before promoting an
            //     aware unit into the chase (`UnitZombie.as:623-675`) — about 40 ticks of "I have
            //     noticed you" before it moves.
            //
            // The port does not model `aiSpok`, so this gate is the stand-in for it and is kept: a
            // pause that is too short is still closer to the oracle than none. It is NOT tuned to
            // `shok`'s range, and it is deliberately left reading the field it always read — but be
            // aware that widening `shok` to the oracle's 5..19 lengthened this pause from at most
            // 0.23 s to at most 0.63 s as a side effect. The oracle's value here is ~40 ticks, so that
            // is a move toward the oracle rather than away from it, but it is a real change and it was
            // not the point of the edit. Closing the gap properly means modelling the `aiSpok`
            // awareness ladder; that is its own task.
            if (_blackboard.ShockTimerTicks > 0)
            {
                StopMovement();
                return;
            }

            float currentX = transform.position.x * 100f;
            float diffX = _blackboard.LastKnownTargetPosition.x - currentX;
            int chaseDir = diffX >= 0 ? 1 : -1;
            _blackboard.FacingDirection = chaseDir;
            ApplyFacing(chaseDir);

            bool inRange = IsTargetInAttackRange();

            if (inRange && _blackboard.AttackCooldownTicks <= 0)
            {
                // The `Attack` state is a port-only construct: AS3's zombies and raiders have no attack
                // state, only a per-frame contact test. A brain that models one opts into it through
                // StopsToAttack; one that does not still gets ExecuteAttackAction every tick, with the
                // target's own `neujaz` window as the rate limit (AS3 Unit.as:3273/4135).
                if (StopsToAttack)
                {
                    SetState(EnemyAIState.Attack);
                }

                ExecuteAttackAction();
            }

            if (StopsToAttack && inRange)
            {
                StopMovement();
                return;
            }

            MoveHorizontal(chaseDir * _chaseSpeed);
        }

        /// <summary>
        /// Whether the target is close enough to swing at, in this archetype's own terms.
        ///
        /// <para><b>The base implementation has no oracle basis and every archetype should override
        /// it.</b> AS3 tests the <i>collision boxes</i>, not a radius — <c>Unit.attKorp()</c> refuses
        /// unless <c>param1.X1 &lt;= X2 &amp;&amp; param1.X2 &gt;= X1 &amp;&amp; …</c>
        /// (<c>Unit.as:3273</c>) — and each attacker additionally guards the attempt with its own
        /// window (<c>UnitZombie.as:938</c> is <c>|celDX| &lt; 200 &amp;&amp; |celDY| &lt; 80</c>). The
        /// distance heuristic below is a placeholder so that a brain which has not ported its own
        /// <c>control()</c> is not silently melee-less; it is not evidence about any unit.</para>
        /// </summary>
        protected virtual bool IsTargetInAttackRange()
        {
            float absDx = Mathf.Abs(_blackboard.TargetDeltaX);
            float absDy = Mathf.Abs(_blackboard.TargetDeltaY);
            return _blackboard.TargetDistance <= _attackRangePixels
                || (absDx <= _attackRangePixels && absDy <= 50f);
        }

        /// <summary>
        /// Whether this archetype halts to land a hit. <c>true</c> for the generic stop-and-swing shape;
        /// <c>false</c> for AS3's contact attackers, which keep running and damage whatever their box
        /// happens to overlap.
        /// </summary>
        protected virtual bool StopsToAttack => true;

        protected virtual void TickAttack(int tickIndex)
        {
            SetState(EnemyAIState.CombatChase);
        }

        /// <summary>
        /// The per-tick behaviour of <see cref="EnemyAIState.Buried"/>.
        ///
        /// <para><b>The base does nothing, and that is a decision rather than a stub.</b> "Buried" is not
        /// a generic enemy state: AS3 defines <c>aiState 5</c> only in <c>UnitZombie.control()</c>
        /// (<c>UnitZombie.as:574-606</c>), where the wake conditions are the zombie's own — the two
        /// floor tiles it was buried under, and the digger tier that decides whether it is allowed to
        /// care. Inventing a generic wake rule here would be porting behaviour the oracle does not have,
        /// and it would be indistinguishable from a real port — the same argument
        /// <see cref="ResolveAnimState"/> makes.</para>
        ///
        /// <para>A brain that does not override this cannot reach the state either: only
        /// <c>ZombieBrain</c> ever calls <c>SetState(EnemyAIState.Buried)</c>. So the no-op is
        /// unreachable in practice, and if a future archetype does enter it, standing still forever is
        /// the honest failure — visible, and traceable to the missing override.</para>
        /// </summary>
        protected virtual void TickBuried(int tickIndex)
        {
        }

        /// <summary>
        /// The per-tick behaviour of <see cref="EnemyAIState.Digging"/>. See
        /// <see cref="TickBuried"/> for why the base does nothing.
        ///
        /// <para>Unlike <see cref="TickBuried"/> this one <i>does</i> have a shared half worth not
        /// duplicating — the timer — so an archetype that opts in can call
        /// <see cref="AdvanceDigTimer"/> and only supply the exit.</para>
        /// </summary>
        protected virtual void TickDigging(int tickIndex)
        {
        }

        /// <summary>
        /// Count the dig down one tick. <c>true</c> once the counter has already reached zero, i.e. the
        /// unit should be standing on this tick — AS3 <c>UnitZombie.control()</c>'s
        /// <c>else if(aiState == 6)</c> arm (<c>UnitZombie.as:586-594</c>).
        ///
        /// <para><b>The countdown and the exit are two separate ticks in the oracle, not one.</b>
        /// <c>aiTCh</c> is decremented by the leading <c>if(aiTCh &gt; 0)</c> and the <c>aiState == 6</c>
        /// branch is an <c>else if</c> of the same chain, so the tick that takes the counter from 1 to 0
        /// takes the <c>if</c> and never reaches the rise; <c>vykop()</c> happens on the <i>next</i> tick.
        /// Collapsing the two — <c>--timer &lt;= 0</c> — makes the dig <b>1 tick short (24 instead of
        /// 25)</b>, which is invisible in play and wrong in a fixture.</para>
        /// </summary>
        protected bool AdvanceDigTimer()
        {
            if (_blackboard.StateTimerTicks > 0)
            {
                _blackboard.StateTimerTicks--;
                return false;
            }

            return true;
        }

        protected abstract void ExecuteAttackAction();

        /// <summary>
        /// Command a horizontal speed, in <b>AS3's units — pixels per 30 Hz frame</b>.
        ///
        /// <para><b>The two consumers do not share a unit, and this method is where that is reconciled.</b>
        /// <see cref="IMovementMotor.SetDesiredHorizontalSpeed"/> assigns straight into the motor's
        /// <c>dx</c>, which is px/frame (<c>TilePhysicsController.cs:544</c>);
        /// <c>UnitController.SetVelocityX</c> assigns into a Unity-units-per-second field
        /// (<c>UnitController.cs:1130</c>). Passing one number to both means the same "3.5" is 105 px/s on
        /// one path and 350 px/s on the other — a 3.33× difference that no test covers, because the motor
        /// path is off by default (<c>RoomUnitSpawner._useTileMotor</c>). Converting here makes the
        /// oracle's own numbers (zombie0 walks at 1.5 px/frame, runs at 10) mean the same thing either
        /// way.</para>
        /// </summary>
        protected virtual void MoveHorizontal(float speedPixelsPerFrame)
        {
            if (_motor != null)
            {
                _motor.SetDesiredHorizontalSpeed(speedPixelsPerFrame);
            }
            else if (_controller != null)
            {
                _controller.SetVelocityX(speedPixelsPerFrame * TileQueryConstants.PerFrameVelocityToUnitsPerSecond);
            }
        }

        protected virtual void StopMovement()
        {
            MoveHorizontal(0f);
        }

        protected virtual void ApplyFacing(int direction)
        {
            if (_controller != null)
            {
                _controller.SetFacing(direction);
            }
        }

        protected virtual Vector2 GetEyePositionPixels()
        {
            // Center of head / upper torso
            float heightPx = (_controller != null && _controller.Stats != null)
                ? _controller.Stats.Height * 100f
                : 50f;
            Vector2 basePosPx = (Vector2)transform.position * 100f;
            return basePosPx + new Vector2(0f, heightPx * 0.5f);
        }

        protected virtual void RefreshTargetCandidates()
        {
            _targetCandidates.Clear();
            var players = FindObjectsByType<PFE.Entities.Player.PlayerController>(FindObjectsSortMode.None);
            for (int i = 0; i < players.Length; i++)
            {
                if (players[i] != null && players[i].IsAlive)
                {
                    _targetCandidates.Add(players[i]);
                }
            }

            if (_blackboard.TargetUnit != null && _blackboard.TargetUnit.IsAlive && !_targetCandidates.Contains(_blackboard.TargetUnit))
            {
                _targetCandidates.Add(_blackboard.TargetUnit);
            }
        }
    }
}
