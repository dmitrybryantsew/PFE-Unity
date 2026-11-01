using System;
using System.Collections.Generic;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Campaign;
using PFE.Systems.Combat;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Physics;
using PFE.Systems.Weapons;
using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    public enum FlyerKind
    {
        Bloat = 0,      // UnitBloat (drift, spit, contact, death emit)
        Bat = 1,        // UnitBat (6-state ladder with dive attack)
        Phoenix = 2,    // UnitPhoenix (tamed prop)
        Emitter = 3     // UnitBloatEmitter (nest spawner)
    }

    /// <summary>
    /// Flyer enemy brain — a port of AS3 <c>fe/unit/UnitBloat.as</c>, <c>UnitBat.as</c>,
    /// <c>UnitPhoenix.as</c>, and <c>UnitBloatEmitter.as</c>.
    ///
    /// <para><b>What was wrong with the previous version, and why it produced three reports.</b> It moved
    /// with <c>MoveHorizontal</c> (an absolute speed) and <c>JumpVertical</c> (a one-shot upward impulse
    /// that also clears groundedness), with an invented <c>sin(tick)</c> hover and an invented 5 % hover
    /// chance. None of that is the oracle's model: AS3 <i>accumulates</i> into <c>dx</c>/<c>dy</c> —
    /// <c>dx += aiDx * accel + random() * 2 - 1</c> — and lets <c>forces()</c> cap and brake the result.
    /// Combined with a missing <c>isFly</c> flag (see <c>FlyerController.IsFlying</c>), a bloat therefore
    /// fell like a stone while its sprite played a fly loop, a bloodwing could never reach its dive speed,
    /// and the nest had no <c>control()</c> at all so it never emitted.</para>
    ///
    /// <para><b>The port's tick vs the oracle's.</b> AS3 runs <c>forces() → control() → run()</c> in one
    /// frame; the port runs this brain at <c>SimTickOrder.UnitMotor - 1</c> and the physics step at
    /// <c>UnitMotor</c>, so the velocity this brain writes is the velocity the step integrates. The brain
    /// reads the <i>live</i> velocity back (<c>UnitController.VelocityPixelsPerFrame</c>) rather than
    /// keeping a copy, so the cap and the deadzone are applied to the same number the step will use.</para>
    ///
    /// <para><b>Two ladders, one field.</b> <c>_familyState</c> is AS3's <c>aiState</c>: bloat 0/1/2, bat
    /// 0..6, emitter 1/2. It is <b>not</b> <see cref="EnemyAIState"/>, and the bat's 4/5/6 mean
    /// pursue/stall/dive — not the zombie's pre/buried/dig. <c>_currentState</c> is written from it once
    /// per tick, for the F3 panel and for the damage/alarm rules that read it.</para>
    /// </summary>
    public class FlyerBrain : EnemyBrain
    {
        [SerializeField] protected FlyerKind _kind = FlyerKind.Bloat;

        /// <summary>
        /// AS3 <c>aiTCh</c> — the cadence that re-rolls the state. Decremented once per tick, and the
        /// re-roll happens on the tick <i>after</i> it reaches zero, exactly as
        /// <c>if(aiTCh &gt; 0) --aiTCh; else { … }</c> does.
        /// </summary>
        protected int _aiTCh;

        /// <summary>AS3 <c>aiSpok</c> — the bat's awareness budget (<c>maxSpok</c> 30).</summary>
        protected int _aiSpok;

        /// <summary>AS3 <c>aiState</c>. See the class remarks for what each value means per kind.</summary>
        protected int _familyState;

        /// <summary>
        /// AS3 <c>aiDx</c>/<c>aiDy</c> — the unit vector (or the ±0.7 drift) the unit steers along,
        /// <b>in AS3's convention</b> where Y grows down. Negated once, on the way into a velocity.
        /// </summary>
        protected float _aiDx;
        protected float _aiDy;

        /// <summary>AS3 <c>aiRasst</c> — the distance at the last decision, in pixels.</summary>
        protected float _aiRasst;

        /// <summary>AS3 <c>emit_t</c> — the nest's emit countdown (<c>UnitBloatEmitter.as:10</c>).</summary>
        protected int _emitTimer;

        /// <summary>AS3 <c>maxSpeed</c> — the definition's walk speed, jittered once at construction.</summary>
        protected float _maxSpeed = 5f;

        /// <summary>AS3 <c>runSpeed</c> — <c>maxSpeed * 2</c> for both families.</summary>
        protected float _runSpeed = 10f;

        /// <summary>AS3 <c>accel</c> — the per-frame thrust added to <c>dx</c>/<c>dy</c>.</summary>
        protected float _accel = 2f;

        /// <summary>AS3 <c>brake</c> — the flyer's deadzone, <c>0.3</c> in the shipped rows.</summary>
        protected float _brake = 0.3f;

        /// <summary>
        /// The live <c>maxSpeed</c> handed to the flight brake. AS3 rewrites <c>maxSpeed</c> every frame
        /// from the state; the physics step has to read the same value or a charge/dive is clamped to a
        /// walk. See <c>UnitController.SetFlySpeedCapPixelsPerFrame</c>.
        /// </summary>
        protected float _speedCap = 5f;

        /// <summary>
        /// The construction-time jitter has already been applied. <see cref="ApplyDefinitionTuning"/> runs
        /// from three places (<c>Awake</c>, <c>Initialize</c>, <c>OnDefinitionAssigned</c>), and AS3's
        /// <c>maxSpeed *= 0.9 + random() * 0.2</c> is a <i>constructor</i> fact — applying it three times
        /// would shrink every bloat by up to 27 % and re-roll it each time.
        /// </summary>
        bool _speedJittered;

        /// <summary>
        /// The nest's live children — AS3 <c>kolChild</c>, which is incremented by <c>emit(true)</c>
        /// (<c>UnitBloatEmitter.as:82</c>) and decremented by the child's own death
        /// (<c>Unit.as:4384-4387</c>). Pruned by liveness rather than by a callback, because the port has
        /// no per-child hook and a dead <c>UnitController</c> is the same information.
        /// </summary>
        readonly List<UnitController> _children = new List<UnitController>();

        public FlyerKind Kind { get => _kind; set => _kind = value; }

        /// <summary>AS3 <c>aiState</c>, exposed for the F3 panel and for fixtures.</summary>
        public int FamilyState => _familyState;

        /// <summary>
        /// AS3 <c>Unit.isFly</c> for this family — set by the constructor of <c>UnitBloat</c>
        /// (<c>:71</c>) and <c>UnitBat</c> (<c>:48</c>), never by the phoenix (parked, <c>:24</c> sets
        /// <c>stay</c>) or the nest (<c>fixed='1'</c>).
        /// </summary>
        public bool IsFlying => _kind == FlyerKind.Bloat || _kind == FlyerKind.Bat;

        protected override bool StopsToAttack => false;

        protected override void Awake()
        {
            base.Awake();
            ApplyDefinitionTuning();
        }

        public override void Initialize(ITileQueryService tileQuery, SimLoop simLoop)
        {
            base.Initialize(tileQuery, simLoop);
            ApplyDefinitionTuning();
        }

        public override void OnDefinitionAssigned()
        {
            base.OnDefinitionAssigned();
            ApplyDefinitionTuning();
        }

        protected virtual void ApplyDefinitionTuning()
        {
            if (_controller == null || _controller.Definition == null) return;
            UnitDefinition def = _controller.Definition;

            _maxSpeed = def.moveSpeed > 0f ? def.moveSpeed : 5f;
            _accel = def.Acceleration > 0f ? def.Acceleration : 2f;
            _brake = UnitFlyPhysics.ResolveBrake(def.braking);

            if (_speedJittered)
            {
                return;
            }

            // `UnitBloat.as:64` — `maxSpeed *= 0.9 + Math.random() * 0.2`, then
            // `walkSpeed = maxSpeed; runSpeed = maxSpeed * 2` (`:66-70`).
            // `UnitBat.as:44` — `maxSpeed += Math.random() * 2 - 1`, then the same two lines (`:45-46`).
            // Two different jitters, both applied once, both before the walk/run split.
            if (_kind == FlyerKind.Bloat)
            {
                _maxSpeed *= 0.9f + UnityEngine.Random.value * 0.2f;
            }
            else if (_kind == FlyerKind.Bat)
            {
                _maxSpeed += UnityEngine.Random.value * 2f - 1f;
            }

            _runSpeed = def.runSpeed > 0f ? def.runSpeed : _maxSpeed * 2f;
            _speedCap = _maxSpeed;
            _speedJittered = true;
        }

        public override void SimTick(int tickIndex)
        {
            // AS3's cadence decrement sits after the freeze gate in every `control()` in the family
            // (`UnitBloat.as:216-222`, `UnitBat.as:130`, `UnitBloatEmitter.as:117-121`), so a frozen
            // flyer's timers stop rather than draining.
            if (!EnemyActivity.IsFrozen && _aiTCh > 0)
            {
                _aiTCh--;
            }

            // Perception, the (mapped) port state tick, the draw and the weapon mounts.
            base.SimTick(tickIndex);

            if (EnemyActivity.IsFrozen || _controller == null || !_controller.IsAlive)
            {
                return;
            }

            switch (_kind)
            {
                case FlyerKind.Bloat:
                    BloatControl(tickIndex);
                    break;
                case FlyerKind.Bat:
                    BatControl(tickIndex);
                    break;
                case FlyerKind.Emitter:
                    EmitterControl(tickIndex);
                    break;
                default:
                    // Phoenix: a tamed prop. `UnitPhoenix.control()` only watches for its own fall
                    // (`UnitPhoenix.as:145-161`), which is blocked on the taming system — see the family
                    // guide's §11. Standing still is the honest failure.
                    StopMovement();
                    break;
            }

            _currentState = MapToPortState(_familyState);
        }

        /// <summary>
        /// AS3 <c>aiState</c> mapped onto the port's enum <b>for reporting only</b> — the F3 panel, and
        /// the damage/alarm rules that ask whether a unit was caught unawares. The movement ladder never
        /// reads it back.
        ///
        /// <para><b>Why a direct field write rather than <see cref="EnemyBrain.SetState"/>.</b>
        /// <c>SetState</c> is the port's own machine: entering <c>CombatChase</c> from Idle/Patrol re-arms
        /// a 5..19-tick <c>shok</c> stagger and shouts to the room, and entering Idle calls
        /// <c>StopMovement</c>. A bloat that drifted into a sightline would then be stunned for half a
        /// second by its own acquisition, and its <c>shok &lt;= 0</c> spit gate would be shut for exactly
        /// the ticks it should be spitting. The oracle's bloat has neither behaviour.</para>
        /// </summary>
        EnemyAIState MapToPortState(int familyState)
        {
            switch (_kind)
            {
                case FlyerKind.Bloat:
                    if (familyState == FlyerControlMath.BloatCharge) return EnemyAIState.CombatChase;
                    if (familyState == FlyerControlMath.BloatStalk) return EnemyAIState.Alert;
                    return EnemyAIState.Idle;

                case FlyerKind.Bat:
                    if (familyState == FlyerControlMath.BatIdle) return EnemyAIState.Idle;
                    if (familyState == FlyerControlMath.BatWander) return EnemyAIState.Patrol;
                    if (familyState == FlyerControlMath.BatMoveToLastKnown) return EnemyAIState.Alert;
                    return EnemyAIState.CombatChase;

                case FlyerKind.Emitter:
                    return familyState == 2 ? EnemyAIState.CombatChase : EnemyAIState.Idle;

                default:
                    return EnemyAIState.Idle;
            }
        }

        // ── Bloat: UnitBloat.control() (UnitBloat.as:201-312) ────────────────────────────────────

        protected virtual void BloatControl(int tickIndex)
        {
            if (_aiTCh <= 0)
            {
                BloatDecide();
            }

            _speedCap = FlyerControlMath.BloatMaxSpeed(_familyState, _maxSpeed, _runSpeed);

            // `dx += this.aiDx * accel + Math.random() * 2 - 1;` and the same for dy (`:276-277`). The
            // jitter is ±1 px/frame against a thrust of `accel * 0.7` = 1.4, which is why a bloat wobbles
            // rather than tracking a line — it is the oracle's own noise, not a decoration.
            float jitterX = UnityEngine.Random.value * 2f - 1f;
            float jitterY = UnityEngine.Random.value * 2f - 1f;
            Fly(_aiDx * _accel + jitterX, -(_aiDy * _accel) + jitterY);

            if (!EnemyActivity.IsActive || _blackboard.TargetUnit == null)
            {
                return;
            }

            // `if(aiState == 1 && currentWeapon) if(isrnd(shootCh)) currentWeapon.attack();` (`:299-305`).
            // The port's bloat rows import no weapon (`weapons: []` on every `bloat*` asset), so this
            // falls back to the direct acid spit that stood here before. Recorded as a data gap, not as
            // the oracle's behaviour: AS3 fires a real projectile from `bloat1`/`ship`/`pinkbloat`.
            if (_familyState == FlyerControlMath.BloatStalk
                && FlyerControlMath.IsRnd(UnityEngine.Random.value, FlyerControlMath.BloatShootChance))
            {
                Spit();
            }

            // `var _loc1_ = attKorp(celUnit);` (`:306`) — contact damage, every tick, self-gated on the
            // AABB overlap and the target's i-frames.
            _controller.TryContactAttack(_blackboard.TargetUnit, 1f);
        }

        /// <summary>
        /// The bloat's <c>aiTCh</c> expiry — <c>UnitBloat.as:220-252</c>.
        /// </summary>
        protected virtual void BloatDecide()
        {
            int timerRoll = UnityEngine.Random.Range(0, FlyerControlMath.BloatReRollRollRange);

            if (_blackboard.TargetUnit != null)
            {
                // `this.aiRasst = Math.sqrt(celDX * celDX + celDY * celDY);` (`:225`). The deltas are the
                // port's `TargetDeltaX/Y`, which are measured eye-to-centre rather than
                // centre-to-centre — a bias of about half a body height, recorded rather than fixed.
                float celDx = _blackboard.TargetDeltaX;
                float celDy = -_blackboard.TargetDeltaY; // AS3's Y grows down
                _aiRasst = Mathf.Sqrt(celDx * celDx + celDy * celDy);

                // `isrnd(attCh)` is short-circuited behind the range test in the oracle, so the draw only
                // happens inside the range. Drawing it unconditionally would shift the stream.
                bool committed = false;
                if (_aiRasst < FlyerControlMath.BloatAttackRangePixels)
                {
                    committed = FlyerControlMath.IsRnd(
                        UnityEngine.Random.value, FlyerControlMath.BloatCommitChance);
                }

                ApplyStep(FlyerControlMath.BloatDecideWithTarget(
                    _aiRasst, celDx, celDy, committed, timerRoll));
                return;
            }

            // `if(isrnd(0.1)) { aiDx = isrnd() ? 0.7 : -0.7; aiDy = isrnd() ? 0.7 : -0.7; }` (`:243-247`).
            // Both sign rolls are drawn only inside the 10 % arm.
            bool redraw = FlyerControlMath.IsRnd(
                UnityEngine.Random.value, FlyerControlMath.BloatDriftRedrawChance);
            float rollX = 0f;
            float rollY = 0f;
            if (redraw)
            {
                rollX = UnityEngine.Random.value;
                rollY = UnityEngine.Random.value;
            }

            ApplyStep(FlyerControlMath.BloatDecideIdle(
                redraw, rollX, rollY, _aiDx, _aiDy, timerRoll));
        }

        /// <summary>
        /// The bloat's ranged attack. See <see cref="BloatControl"/> for why this is a direct hit rather
        /// than the oracle's projectile.
        /// </summary>
        void Spit()
        {
            if (_blackboard.TargetUnit == null || _controller == null) return;

            float baseDamage = _controller.MeleeDamage;
            Vector2 targetPos = (Vector2)_blackboard.TargetUnit.transform.position * 100f;

            var pending = PendingDamage.Direct(
                DamageContext.Contact(baseDamage, DamageType.Acid),
                _blackboard.TargetUnit,
                targetPos);

            if (_controller.DamageSystem != null)
            {
                _controller.DamageSystem.Report(pending);
            }
            else
            {
                _controller.TryContactAttack(_blackboard.TargetUnit, 1f);
            }
        }

        // ── Bat: UnitBat.control() (UnitBat.as:116-291) ──────────────────────────────────────────

        protected virtual void BatControl(int tickIndex)
        {
            UpdateBatAwareness(tickIndex);

            if (_aiTCh <= 0)
            {
                BatDecide();
            }

            float? cap = FlyerControlMath.BatMaxSpeed(_familyState, _maxSpeed, _runSpeed);
            if (cap.HasValue)
            {
                _speedCap = cap.Value;
            }

            if (FlyerControlMath.BatThrusts(_familyState))
            {
                // `dx += this.aiDx * accel + Math.random() * 2 - 1;` (`:259-260`), and `* 3` in the dive
                // (`:270-271`).
                float factor = FlyerControlMath.BatThrustFactor(_familyState);
                float jitterX = UnityEngine.Random.value * 2f - 1f;
                float jitterY = UnityEngine.Random.value * 2f - 1f;
                Fly(_aiDx * _accel * factor + jitterX, -(_aiDy * _accel * factor) + jitterY);
            }
            else
            {
                // States 0 and 5 add no thrust; the flight brake is what stops the unit. Calling Fly with
                // a zero thrust keeps the cap, the brake and the command in one place — the oracle's
                // `forces()` runs either way.
                Fly(0f, 0f);
            }

            ExecuteBatAttacks();
        }

        /// <summary>
        /// The <c>findCel()</c> block of <c>UnitBat.as:188-221</c>, on its own 3 Hz cadence.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the promotion lives here and not in the ladder.</b> A bat that spots the player is
        /// promoted to 4 (or 3 at full health) <i>immediately</i>, not when its <c>aiTCh</c> next expires
        /// — the expiry cycle is 100..199 ticks, so a ladder-only port would leave a bat hovering in state
        /// 2 for up to six seconds after it had already acquired the player. That is the difference
        /// between a bat that dives and a bat that drifts.</para>
        ///
        /// <para><b>Cadence.</b> The oracle gates on <c>aiTCh % 10 == 1</c>; this uses
        /// <c>tickIndex % 10 == 0</c>, the port's own phase (recorded in the family guide §5). The rate is
        /// the same 3 Hz.</para>
        /// </remarks>
        protected virtual void UpdateBatAwareness(int tickIndex)
        {
            if (tickIndex % 10 != 0)
            {
                return;
            }

            bool wounded = _controller != null && _controller.CurrentHealth < _controller.MaxHealth;

            if (_blackboard.TargetUnit != null)
            {
                // `aiSpok = maxSpok + 10;` (`:192`).
                _aiSpok = FlyerControlMath.BatAwarenessOnSight;

                // `if(aiState < 5) aiState = hp < maxhp ? 4 : 3;` (`:193-196`).
                if (_familyState < FlyerControlMath.BatStall)
                {
                    _familyState = wounded ? FlyerControlMath.BatPursue : FlyerControlMath.BatChase;
                }
            }
            else if (_aiSpok > 0)
            {
                _aiSpok--;
            }

            // `this.aiRasst = Math.sqrt(aiDx * aiDx + aiDy * aiDy);` and the direction re-derivation, for
            // aiState 2..5 (`:202-220`). One expression for both: the vector to the target.
            if (FlyerControlMath.BatSteersAtTarget(_familyState) && _blackboard.TargetUnit != null)
            {
                float celDx = _blackboard.TargetDeltaX;
                float celDy = -_blackboard.TargetDeltaY;
                float length = Mathf.Sqrt(celDx * celDx + celDy * celDy);

                if (length > 0.0001f)
                {
                    _aiDx = celDx / length;
                    _aiDy = celDy / length;
                    _aiRasst = length;
                }

                if (_aiDx > 0f) ApplyFacing(1);
                else if (_aiDx < 0f) ApplyFacing(-1);
            }
        }

        /// <summary>
        /// The bat's <c>aiTCh</c> expiry — <c>UnitBat.as:130-187</c>, arm for arm, drawing exactly what
        /// each arm draws.
        /// </summary>
        protected virtual void BatDecide()
        {
            // `else if(aiState == 6) { aiState = 4; aiTCh = random*100+100; }`
            if (_familyState == FlyerControlMath.BatDive)
            {
                ApplyStep(FlyerControlMath.BatArms.DiveRecover(
                    UnityEngine.Random.Range(0, FlyerControlMath.BatReRollRollRange)));
                return;
            }

            // `else if(aiState == 5) { aiState = 6; aiTCh = 20; }` — no draw.
            if (_familyState == FlyerControlMath.BatStall)
            {
                ApplyStep(FlyerControlMath.BatArms.StallBecomeDive());
                return;
            }

            // `else if(aiSpok == 0)` — it has lost track of everything.
            if (_aiSpok <= 0)
            {
                float wanderRoll = _familyState > FlyerControlMath.BatWander
                    ? UnityEngine.Random.value
                    : 0f;
                ApplyStep(FlyerControlMath.BatArms.AwarenessLost(_familyState, wanderRoll));
                return;
            }

            // `else if(aiSpok >= maxSpok)`
            if (_aiSpok >= FlyerControlMath.BatMaxAwareness)
            {
                if (_aiRasst < FlyerControlMath.BatStallRangePixels)
                {
                    // `if(!isPlav && isrnd()) { … 5 … } else { … 4 … }` — one draw, in the coin.
                    ApplyStep(FlyerControlMath.BatArms.AwareClose(
                        UnityEngine.Random.value < 0.5f,
                        UnityEngine.Random.Range(0, FlyerControlMath.BatReRollRollRange)));
                    return;
                }

                bool wounded = _controller != null && _controller.CurrentHealth < _controller.MaxHealth;
                ApplyStep(FlyerControlMath.BatArms.AwareFar(
                    wounded, UnityEngine.Random.Range(0, FlyerControlMath.BatReRollRollRange)));
                return;
            }

            // `else { aiState = 2; }` — aware but not committed: go and look.
            ApplyStep(FlyerControlMath.BatArms.Uncommitted(
                UnityEngine.Random.Range(0, FlyerControlMath.BatReRollRollRange)));
        }

        /// <summary>
        /// <c>UnitBat.as:273-290</c> — contact damage in states 3, 4 and 6, and the dive's bleed.
        /// </summary>
        /// <remarks>
        /// <para><b>The bleed is not applied, and this is a named gap rather than an omission.</b> The
        /// oracle's state-6 hit does <c>celUnit.cut += dam / 3</c> (<c>:287</c>), and <c>cut</c> ticks down
        /// into <c>damage(sqrt(cut), D_BLEED)</c> every 30 frames (<c>Unit.as:3160-3173</c>). The port has
        /// no <c>cut</c> field on <c>UnitController</c> and no bleed tick, so there is nothing to add to;
        /// inventing a one-shot bleed would be a different rule with the same name. The 2× damage <i>is</i>
        /// applied.</para>
        /// </remarks>
        protected virtual void ExecuteBatAttacks()
        {
            if (!EnemyActivity.IsActive) return;

            UnitController target = _blackboard.TargetUnit;
            if (target == null) return;

            bool staggered = _blackboard.ShockTimerTicks > 0;

            if (_familyState == FlyerControlMath.BatChase && !staggered)
            {
                // `if(aiState == 3 && shok <= 0 && isrnd(0.1)) attKorp(celUnit);` — a healthy bat only
                // lands a contact every tenth tick or so; the state-4 bat lands one every tick.
                if (FlyerControlMath.IsRnd(UnityEngine.Random.value, FlyerControlMath.BatChaseContactChance))
                {
                    _controller.TryContactAttack(target, 1f);
                }

                return;
            }

            if (_familyState == FlyerControlMath.BatPursue && !staggered)
            {
                _controller.TryContactAttack(target, 1f);
                return;
            }

            if (_familyState == FlyerControlMath.BatDive)
            {
                // `if(attKorp(celUnit,2) && !celUnit.invulner)` — note the dive is NOT gated on `shok`.
                _controller.TryContactAttack(target, 2f);
            }
        }

        // ── Emitter: UnitBloatEmitter.control() (UnitBloatEmitter.as:111-146) ────────────────────

        protected virtual void EmitterControl(int tickIndex)
        {
            // `if(aiTCh > 0) --aiTCh; if(enemyAct > 1 && aiTCh == 0) { aiTCh = 10; aiState = findCel() ? 2 : 1; }`
            if (_aiTCh == 0 && EnemyActivity.RunsCadence)
            {
                _aiTCh = FlyerControlMath.EmitterProbeTicks;
                _familyState = _blackboard.TargetUnit != null ? 2 : 1;
            }

            PruneChildren();

            if (_emitTimer > 0)
            {
                _emitTimer--;
            }
            else if (FlyerControlMath.EmitterShouldEmit(_emitTimer, _familyState, _children.Count))
            {
                // `this.emit(true); this.emit_t = 100;` — the emit is NOT gated on `enemyAct`; only the
                // probe above is. A frozen nest never reaches here anyway (SimTick returns early).
                EmitOne(registerChild: true);
                _emitTimer = FlyerControlMath.EmitterEmitTicks;
            }

            if (EnemyActivity.IsActive && _blackboard.TargetUnit != null)
            {
                _controller.TryContactAttack(_blackboard.TargetUnit, 1f);
            }
        }

        /// <summary>
        /// <c>UnitBloatEmitter.emit()</c> (<c>:63-85</c>) — one child at the nest's own position.
        /// </summary>
        /// <remarks>
        /// <c>emitId</c> is <c>"bloat"</c> for this family (<c>eant</c>'s ant nest is out of scope), and
        /// the variant is <c>"0"</c> unless the room is above difficulty 3, where the oracle calls
        /// <c>randomCid("bloat")</c>. That roll is transcribed in
        /// <see cref="FlyerControlMath.BloatVariantRoll"/>.
        /// </remarks>
        protected UnitController EmitOne(bool registerChild)
        {
            if (_controller == null) return null;

            RoomInstance room = _controller.Room;
            string unitId = "bloat0";

            if (room != null)
            {
                float difficulty = RoomPopulator.EnemyContextFor(room).Difficulty;
                if (difficulty > 3f)
                {
                    int variant = FlyerControlMath.BloatVariantRoll(
                        room.biome, difficulty, UnityEngine.Random.value);
                    unitId = "bloat" + variant;
                }
            }

            UnitController child = FlyerSpawner.Spawn(room, unitId, _controller.transform.position);
            if (child != null && registerChild)
            {
                _children.Add(child);
            }

            return child;
        }

        void PruneChildren()
        {
            for (int i = _children.Count - 1; i >= 0; i--)
            {
                UnitController child = _children[i];
                if (child == null || !child.IsAlive)
                {
                    _children.RemoveAt(i);
                }
            }
        }

        /// <summary>Live children — AS3 <c>kolChild</c>. Exposed for the F3 panel and for fixtures.</summary>
        public int ChildCount => _children.Count;

        /// <summary>
        /// <c>dropLoot()</c> for the nest (<c>UnitBloatEmitter.as:100-109</c>) — five children, none of
        /// which counts toward <c>kolChild</c> (the oracle's <c>emit()</c> is called with its default
        /// <c>param1 = false</c>).
        /// </summary>
        public void EmitOnDeath()
        {
            for (int i = 0; i < FlyerControlMath.EmitterDeathEmits; i++)
            {
                EmitOne(registerChild: false);
            }
        }

        /// <summary>
        /// <c>dropLoot()</c> for a bloat (<c>UnitBloat.as:165-186</c>) — the <c>tr &gt;= 8</c> split and
        /// the <c>&lt;un bulb&gt;</c> emit. Called by <c>BloatController.OnDeath</c>, which is where the
        /// variant is known.
        /// </summary>
        /// <param name="splitInto">
        /// The unit id each split child becomes (<c>bloat{tr-1}</c>), or <c>null</c> for no split.
        /// </param>
        /// <param name="splitCount">How many split children — the oracle's two.</param>
        /// <param name="emitCount">How many <c>bloat0</c> children the <c>bulb</c> flag emits.</param>
        public void SpawnDeathChildren(string splitInto, int splitCount, int emitCount)
        {
            if (_controller == null) return;

            RoomInstance room = _controller.Room;
            for (int i = 0; i < splitCount; i++)
            {
                FlyerSpawner.Spawn(room, splitInto, _controller.transform.position);
            }

            for (int i = 0; i < emitCount; i++)
            {
                EmitOne(registerChild: false);
            }
        }

        // ── The shared flight step ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Add one frame of thrust to this unit's live velocity and command the result — AS3's
        /// <c>dx += …</c> in <c>control()</c>, with <c>forces()</c>'s cap and deadzone applied first, in the
        /// order the oracle applies them.
        /// </summary>
        /// <param name="thrustX">The horizontal thrust, already in AS3's units (px/frame).</param>
        /// <param name="thrustY">
        /// The vertical thrust in the <b>port's</b> convention (up-positive) — the caller mirrors the
        /// oracle's <c>aiDy</c> sign, because that is the only place the two spaces meet.
        /// </param>
        protected void Fly(float thrustX, float thrustY)
        {
            Vector2 velocity = _controller.VelocityPixelsPerFrame;

            // `forces()` runs BEFORE `control()` in AS3, so the cap and the deadzone act on the velocity
            // the previous frame left behind and this frame's thrust is added afterwards. Doing it the
            // other way round would zero the thrust itself whenever it was under `brake`, and a bat's
            // state-6 thrust of 6 px/frame would survive while a bloat's 1.4 would not.
            velocity = UnitFlyPhysics.ApplyBrake(velocity, _speedCap, _brake, 1f);
            velocity.x += thrustX;
            velocity.y += thrustY;

            // The physics step applies the same brake again on the value it is handed. Handing it the live
            // cap is what makes that second pass a no-op instead of a clamp back to the definition's walk
            // speed.
            _controller.SetFlySpeedCapPixelsPerFrame(_speedCap);

            // ── Why the velocity is ASSIGNED as well as commanded ────────────────────────────────
            //
            // `MoveHorizontal` is a *desired* speed, and the motor's `ApplyHorizontalMovement` reaches it
            // with `MoveTowards(dx, desired, acceleration * rate)` — a 2 px/frame² ramp. AS3's thrust is
            // instantaneous (`dx += …`), so commanding alone would apply a bat's state-6 thrust of 6
            // px/frame at 2 px/frame per tick and turn a dive into a long glide. Assigning the velocity
            // first makes the `MoveTowards` a no-op (its target already equals `dx`), while `MoveHorizontal`
            // still has to be called or the motor would drag `dx` back toward the *previous* command.
            _controller.SetVelocityPixelsPerFrame(velocity);
            MoveHorizontal(velocity.x);

            // The vertical half needs no assignment: the motor consumes this after the gravity pass and
            // assigns `dy` outright (`TilePhysicsController.cs:988-992`), which is the same ordering AS3
            // gives `forces()` → `control()`.
            SetVerticalSpeed(velocity.y);
        }

        /// <summary>Copy a ladder decision onto this brain's fields.</summary>
        protected void ApplyStep(in FlyerStep step)
        {
            _familyState = step.State;
            _aiTCh = step.Timer;

            if (step.RedrawDirection)
            {
                _aiDx = step.DirX;
                _aiDy = step.DirY;
            }

            if (step.Facing != 0)
            {
                ApplyFacing(step.Facing);
            }
        }

        // ── The port state machine, mapped and inert ────────────────────────────────────────────
        //
        // The base class's five states drive the walkers' movement. A flyer's movement is the oracle's own
        // ladder, so every one of these is reduced to the transition that keeps _currentState meaningful
        // for the panel. None of them may move the unit: base.TickAlert would call MoveHorizontal toward
        // the last known position and base.TickCombatChase would call it toward the target, both at the
        // walkers' speeds.

        protected override void TickIdle(int tickIndex)
        {
            if (_blackboard.TargetUnit != null && _blackboard.HasLineOfSight)
            {
                _currentState = EnemyAIState.CombatChase;
            }
        }

        protected override void TickPatrol(int tickIndex)
        {
            TickIdle(tickIndex);
        }

        protected override void TickAlert(int tickIndex)
        {
            if (_blackboard.TargetUnit != null && _blackboard.HasLineOfSight)
            {
                _currentState = EnemyAIState.CombatChase;
            }
        }

        protected override void TickCombatChase(int tickIndex)
        {
            if (ChaseBudgetExhausted)
            {
                _currentState = EnemyAIState.Alert;
            }
        }

        // ── The abstract attack contract ────────────────────────────────────────────────────────
        //
        // `StopsToAttack` is false, so `TryEnterAttackState` never enters the port's Attack state and never
        // calls ExecuteAttackAction — the oracle's attacks are driven by the state ladder above, which is
        // where the contact/fire gates actually live (a bat's contact chance depends on its aiState, and
        // no single "in range" test can express that).

        protected override bool IsTargetInAttackRange()
        {
            if (_blackboard.TargetUnit == null) return false;

            if (_kind == FlyerKind.Bat)
            {
                return _aiRasst < FlyerControlMath.BatStallRangePixels;
            }

            return _aiRasst < FlyerControlMath.BloatAttackRangePixels;
        }

        protected override void ExecuteAttackAction()
        {
            // Unreachable while `StopsToAttack` is false — see the block comment above. Kept as the
            // abstract contract's implementation and pointed at the same two methods the ladder uses, so a
            // future change to `StopsToAttack` cannot silently produce a second, different attack rule.
            if (_kind == FlyerKind.Bat)
            {
                ExecuteBatAttacks();
                return;
            }

            if (_kind == FlyerKind.Bloat && _blackboard.TargetUnit != null)
            {
                _controller.TryContactAttack(_blackboard.TargetUnit, 1f);
            }
        }

        // ── Animation ───────────────────────────────────────────────────────────────────────────

        protected override string ResolveAnimState()
        {
            if (_controller == null) return null;

            if (!_controller.IsAlive)
            {
                return _controller.IsGrounded ? "fall" : "die";
            }

            if (_kind == FlyerKind.Bat)
            {
                // `UnitBat.as:72-103`, in the oracle's own order — the dead branch above is `sost` 2/3,
                // and what is left is the aiState ladder:
                //
                //   if(aiState == 0)      vis.osn.gotoAndStop("stay");
                //   else if(aiState == 6) vis.osn.gotoAndStop("attack");
                //   else                  vis.osn.gotoAndStop("fly");
                //
                // The port's `_familyState` IS that `aiState`. Gated on the row existing, the same way the
                // zombie gates `pre`/`super`: a variant whose clip authors no `attack` segment would
                // otherwise be switched to an empty state, which holds the last drawn cell for the whole
                // dive instead of flying through it.
                if (_familyState == FlyerControlMath.BatIdle)
                {
                    return "stay";
                }

                if (_familyState == FlyerControlMath.BatDive)
                {
                    return _animator != null && _animator.HasState("attack") ? "attack" : "fly";
                }

                return "fly";
            }

            // Bloat: `animate()` is empty (`UnitBloat.as:197-199`) — the symbol clip animates itself, and
            // the imported `osn` clip is a single looping sequence with no state labels. Phoenix: parked on
            // `stay` (`UnitPhoenix.as:60-69`, aiState 0). Emitter: `vis.stop()` in its constructor.
            return "stay";
        }
    }
}
