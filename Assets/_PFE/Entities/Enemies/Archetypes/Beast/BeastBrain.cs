using System;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Combat;
using PFE.Systems.Weapons;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    public enum BeastKind
    {
        Critter = 0,    // UnitMonstrik (rat, molerat, scorp1..3, tarakan)
        Hound = 1,      // UnitHellhound (hellhound1, fast quadruped)
        Slime = 2       // UnitSlime (slime, cryoslime, pinkslime)
    }

    /// <summary>
    /// Beast enemy brain — a high-fidelity port of AS3 <c>fe/unit/UnitMonstrik.as</c>, <c>UnitHellhound.as</c>,
    /// and <c>UnitSlime.as</c>.
    /// </summary>
    public class BeastBrain : EnemyBrain
    {
        [SerializeField] protected BeastKind _kind = BeastKind.Critter;
        [SerializeField] protected bool _isScorpion;
        [SerializeField] protected bool _isCeilingHanging;

        protected int _aiTCh;
        protected int _jumpCooldownTicks;
        protected int _spitTimer;
        protected int _tPunch;
        protected int _familyState; // Critter: 0..3; Hound: 0..3; Slime: 0..2

        public BeastKind Kind
        {
            get
            {
                if (_controller is HellhoundController || GetComponent<HellhoundController>() != null) return BeastKind.Hound;
                if (_controller is SlimeController || GetComponent<SlimeController>() != null) return BeastKind.Slime;
                return _kind;
            }
            set => _kind = value;
        }
        public bool IsCeilingHanging { get => _isCeilingHanging; set => _isCeilingHanging = value; }
        public int FamilyState => _familyState;

        protected override bool StopsToAttack => false;

        public override void AttachController(UnitController controller)
        {
            base.AttachController(controller);
            if (controller is HellhoundController) _kind = BeastKind.Hound;
            else if (controller is SlimeController) _kind = BeastKind.Slime;
            else if (controller is MonstrikController) _kind = BeastKind.Critter;
        }

        protected override void Awake()
        {
            base.Awake();
            if (_controller is HellhoundController) _kind = BeastKind.Hound;
            else if (_controller is SlimeController) _kind = BeastKind.Slime;
            else if (_controller is MonstrikController) _kind = BeastKind.Critter;
            ApplyDefinitionTuning();
        }

        public override void Initialize(ITileQueryService tileQuery, SimLoop simLoop)
        {
            base.Initialize(tileQuery, simLoop);
            ApplyDefinitionTuning();
            if (_kind == BeastKind.Slime && _tileQuery != null)
            {
                // Ceiling check (UnitSlime.as:93: loc.getAbsTile(param2, param3 - 50).phis)
                Vector2 pos = (Vector2)transform.position * 100f;
                if (_tileQuery.CheckCollision(new Rect(pos.x - 5f, pos.y + 40f, 10f, 10f), TileQueryOptions.Default))
                {
                    _isCeilingHanging = true;
                }
            }
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

            float spd = def.moveSpeed > 0 ? def.moveSpeed : 2f;
            float runMult = def.runMultiplier > 0 ? def.runMultiplier : 2f;

            if (_kind == BeastKind.Hound)
            {
                _patrolSpeed = spd;
                _chaseSpeed = Mathf.Max(spd * 2f, 10f); // Hound fast pursuit
                _alertSpeed = _chaseSpeed * 0.7f;
            }
            else if (_kind == BeastKind.Slime)
            {
                _patrolSpeed = spd;
                _chaseSpeed = spd * 1.2f;
                _alertSpeed = spd;
            }
            else // Critter
            {
                _patrolSpeed = spd;
                _chaseSpeed = spd * runMult;
                _alertSpeed = _chaseSpeed;

                string id = def.id != null ? def.id.ToLowerInvariant() : "";

                // Assigned, not one-way set. `ApplyDefinitionTuning` re-runs on every definition change
                // (Awake, Initialize, OnDefinitionAssigned) and `_kind` is re-derived from the controller
                // two lines up, so this has to be re-derived too. As a one-way `if (...) = true` a pooled
                // object that had once been a scorpion kept the flag — and with it the `attack` animation
                // return, on a unit whose sheet authors no `attack` row, which draws nothing.
                _isScorpion = id.Contains("scorp");
            }
        }

        public override void SimTick(int tickIndex)
        {
            _jumpCooldownTicks = UnitJumpMath.Tick(_jumpCooldownTicks);
            if (_aiTCh > 0) _aiTCh--;
            if (_spitTimer > 0) _spitTimer--;
            if (_tPunch > 0) _tPunch--;

            base.SimTick(tickIndex);

            if (_kind == BeastKind.Hound && _controller != null && _controller.IsAlive)
            {
                UpdateHoundLeap(tickIndex);
            }
        }

        protected virtual void UpdateHoundLeap(int tickIndex)
        {
            if (!_controller.IsGrounded || _jumpCooldownTicks > 0) return;

            // Leap over obstacles if chasing target
            if (_currentState == EnemyAIState.CombatChase && _blackboard.HasLastKnownTargetPosition)
            {
                bool targetAbove = _blackboard.TargetDeltaY < -30f;
                Vector2 posPx = (Vector2)transform.position * 100f;
                int facing = _controller.FacingDirection >= 0 ? 1 : -1;

                if (targetAbove || UnitJumpMath.ShouldJump(posPx.y + 20f, _blackboard.LastKnownTargetPosition.y))
                {
                    JumpVertical(10f); // Fast hound leap
                    _jumpCooldownTicks = UnitJumpMath.CooldownTicks(UnityEngine.Random.Range(0, 50));
                }
            }
        }

        protected override void TickIdle(int tickIndex)
        {
            _familyState = 0;
            if (_aiTCh <= 0)
            {
                _aiTCh = UnityEngine.Random.Range(40, 90);
                SetState(EnemyAIState.Patrol);
                return;
            }

            if (_blackboard.HasLineOfSight && _blackboard.TargetUnit != null)
            {
                if (_kind == BeastKind.Hound)
                {
                    Budilo(400f); // Howl on aggro
                }
                SetState(EnemyAIState.CombatChase);
                return;
            }

            if (_blackboard.HasHeardNoise)
            {
                SetState(EnemyAIState.Alert);
                return;
            }

            StopMovement();
        }

        protected override void TickPatrol(int tickIndex)
        {
            _familyState = 1;
            if (_blackboard.HasLineOfSight && _blackboard.TargetUnit != null)
            {
                if (_kind == BeastKind.Hound)
                {
                    Budilo(400f);
                }
                SetState(EnemyAIState.CombatChase);
                return;
            }

            if (_blackboard.HasHeardNoise)
            {
                SetState(EnemyAIState.Alert);
                return;
            }

            if (_aiTCh <= 0)
            {
                _aiTCh = UnityEngine.Random.Range(40, 90);
                SetState(EnemyAIState.Idle);
                return;
            }

            HandleLedgeAhead(tickIndex);
            float dir = (_controller != null && _controller.FacingDirection < 0) ? -1f : 1f;
            MoveHorizontal(dir * _patrolSpeed);
        }

        protected override void TickAlert(int tickIndex)
        {
            _familyState = 2;
            base.TickAlert(tickIndex);
        }

        protected override void TickCombatChase(int tickIndex)
        {
            _familyState = 3;
            if (_blackboard.TargetUnit == null)
            {
                SetState(EnemyAIState.Alert);
                return;
            }

            float dist = _blackboard.TargetDistance;
            float dx = _blackboard.TargetDeltaX;

            if (dx > 5f) ApplyFacing(1);
            else if (dx < -5f) ApplyFacing(-1);

            // Scorpion punch state
            if (_isScorpion && dist <= 80f && Mathf.Abs(_blackboard.TargetDeltaY) <= 40f && UnityEngine.Random.value < 0.2f)
            {
                _familyState = 3; // Punch
            }

            // Critter leap
            if (_kind == BeastKind.Critter && dist <= 120f && dist >= 50f && _controller.IsGrounded && _jumpCooldownTicks <= 0)
            {
                JumpVertical(5f);
                MoveHorizontal((dx >= 0 ? 1f : -1f) * _chaseSpeed * 1.3f);
                _jumpCooldownTicks = 60;
            }
            else
            {
                float dir = dx >= 0 ? 1f : -1f;
                MoveHorizontal(dir * _chaseSpeed);
            }

            if (IsTargetInAttackRange())
            {
                ExecuteAttackAction();
            }
        }

        protected override bool IsTargetInAttackRange()
        {
            if (_blackboard.TargetUnit == null) return false;
            float dist = _blackboard.TargetDistance;

            if (_kind == BeastKind.Slime && _isCeilingHanging)
            {
                return dist <= 250f && Mathf.Abs(_blackboard.TargetDeltaX) <= 60f;
            }

            return dist <= 100f && Mathf.Abs(_blackboard.TargetDeltaY) <= 60f;
        }

        protected override void ExecuteAttackAction()
        {
            if (_controller == null || !_controller.IsAlive) return;

            if (_kind == BeastKind.Slime && _isCeilingHanging)
            {
                // Ceiling slime spits acid down
                if (_spitTimer <= 0)
                {
                    _spitTimer = UnityEngine.Random.Range(45, 80);
                    _controller.MakeNoise(80, true);

                    float acidDmg = _controller.Definition != null ? _controller.Definition.damage : 8f;
                    Vector2 targetPos = (Vector2)_blackboard.TargetUnit.transform.position * 100f;

                    var pending = PendingDamage.Direct(
                        DamageContext.Contact(acidDmg, DamageType.Acid),
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
                return;
            }

            if (_isScorpion && _tPunch <= 0)
            {
                _tPunch = 15; // UnitMonstrik.as:508
                _controller.MakeNoise(120, true);
                _controller.TryContactAttack(_blackboard.TargetUnit, 1.8f);
                return;
            }

            // Contact pressure attack
            float scale = (_isScorpion && _familyState == 3) ? 1.5f : 1f;
            _controller.TryContactAttack(_blackboard.TargetUnit, scale);
        }

        /// <summary>
        /// Ports three oracle <c>animate()</c>s, because this brain serves three families —
        /// <c>UnitMonstrik.as:99-154</c> (Critter), <c>UnitHellhound.as:610-678</c> (Hound) and
        /// <c>UnitSlime</c> (Slime).
        ///
        /// <para><b>The Critter death arm is gated on <c>trup</c>, and for two of its units it is
        /// unreachable.</b> <c>UnitMonstrik.animate():102</c> opens with
        /// <c>if(trup &amp;&amp; (sost == 2 || sost == 3))</c>. <c>rat</c> and <c>tarakan</c> both author
        /// <c>&lt;param trup='0'/&gt;</c> <i>and</i> author no <c>die</c>/<c>death</c> row — the two facts
        /// corroborate each other, because a reachable arm would make AS3's
        /// <c>anims[animState].restart()</c> a TypeError on an unregistered id. So a dead rat keeps
        /// playing its live state, which is what "the corpse stops moving" looks like here. This used to
        /// return <c>"die"</c>, which asked the animator for a row the sheet does not have, and the rat
        /// drew <b>nothing</b>. <c>molerat</c> and <c>scorp1..3</c> are the same class with no
        /// <c>trup</c> attribute, so for them the arm <i>is</i> reachable and they do author both
        /// rows.</para>
        ///
        /// <para><b>The Slime has no <c>animate()</c> at all.</b> <c>UnitSlime</c> overrides neither
        /// <c>animate()</c> nor anything else that writes <c>animState</c>, so it inherits the empty
        /// <c>Unit.animate()</c> (<c>Unit.as:2936-2938</c>) and keeps the <c>"stay"</c> its constructor
        /// set (<c>:2860</c>) for its entire life, alive or dead. It is drawn from the vector class
        /// <c>visualSlime</c> and animated by <c>vis.gotoAndPlay</c>, a MovieClip call — so there is no
        /// blit row for this brain to name either way. The old <c>"die"</c> return was invented.</para>
        /// </summary>
        protected override string ResolveAnimState()
        {
            if (_controller == null) return null;

            // UnitSlime overrides no animate(), so `animState` never leaves its constructor's "stay".
            if (_kind == BeastKind.Slime)
            {
                return "stay";
            }

            if (_kind == BeastKind.Hound)
            {
                // UnitHellhound.as:611-628. This arm has NO `trup` gate (`grep -c trup
                // UnitHellhound.as` is 0), which is exactly why hellhound/hellhound1 author `die`,
                // `death` AND `fall`. The port used to stop at `die`, so the grounded `die -> fall`
                // transition — the body settling after it lands — never played.
                if (!_controller.IsAlive)
                {
                    if (!_controller.IsGrounded) return "death";
                    if (ShowingAnimState("fall")) return "fall";   // the oracle leaves a fall alone
                    return ShowingAnimState("death") ? "fall" : "die";
                }

                if (_controller.IsGrounded)
                {
                    float spd = Mathf.Abs(_motor != null ? _motor.State.Velocity.x : 0f);
                    if (spd > 1.8f) return "run";
                    if (spd > 0.3f) return "walk";
                    return "stay";
                }

                return "jump";
            }

            // ── Critter — UnitMonstrik.as:102, `trup`-gated ────────────────────────────────────────
            if (!_controller.IsAlive && HasReachableDeathAnimation)
            {
                // The oracle's `stay && animState != "death"`.
                return _controller.IsGrounded && !ShowingAnimState("death") ? "die" : "death";
            }

            // `else if(this.t_punch > 0) animState = "attack"` (UnitMonstrik.as:113). The punch takes
            // priority over the movement rows. Only the scorpions set `_tPunch` (see `_isScorpion`
            // above) and only scorp1..3 author an `attack` row (AllData.as:1150/1174/1198), so the two
            // agree — which is the pair the animation lint checks.
            if (_tPunch > 0)
            {
                return "attack";
            }

            if (_controller.IsGrounded)
            {
                float spd = Mathf.Abs(_motor != null ? _motor.State.Velocity.x : 0f);
                if (spd < 0.1f) return "stay";
                if (spd > 1.5f || _currentState == EnemyAIState.CombatChase) return "run";
                return "walk";
            }

            return "jump";
        }
    }
}
