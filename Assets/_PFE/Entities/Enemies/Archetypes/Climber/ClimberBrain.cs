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
    /// <summary>
    /// Climber enemy brain — a high-fidelity port of AS3 <c>fe/unit/UnitAnt.as</c>.
    /// Wall/stair grip (isLaz), 90-degree visual rotation (vstorona), leap attacks, and ant3 ranged spit.
    /// </summary>
    public class ClimberBrain : EnemyBrain
    {
        [SerializeField] protected bool _isClimbing;
        [SerializeField] protected int _vstorona; // 0: floor, -1: left wall, 1: right wall, 2: ceiling
        [SerializeField] protected bool _hasRangedWeapon; // ant3 (antfire)
        [SerializeField] protected float _optDistAtt = 100f;

        protected int _aiTCh;
        protected int _aiNeedLaz; // -1: down, 0: none, 1: up
        protected int _jumpCooldownTicks;
        protected int _attackTimer;
        protected int _familyState; // 0: Idle, 1: Patrol, 2: Alert/Chase, 3: Ranged

        public bool IsClimbing => _isClimbing;
        public int VStorona => _vstorona;
        public int FamilyState => _familyState;

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

            float spd = def.moveSpeed > 0 ? def.moveSpeed : 4f;
            float runMult = def.runMultiplier > 0 ? def.runMultiplier : 2f;
            _patrolSpeed = spd;
            _chaseSpeed = spd * runMult;
            _alertSpeed = _chaseSpeed;

            if (def.weapons != null && def.weapons.Length > 0)
            {
                _hasRangedWeapon = true;
            }
        }

        public override void SimTick(int tickIndex)
        {
            _jumpCooldownTicks = UnitJumpMath.Tick(_jumpCooldownTicks);
            if (_aiTCh > 0) _aiTCh--;
            if (_attackTimer > 0) _attackTimer--;

            base.SimTick(tickIndex);

            UpdateClimbPhysics(tickIndex);
        }

        protected virtual void UpdateClimbPhysics(int tickIndex)
        {
            if (_controller == null || !_controller.IsAlive) return;

            // Check if near vertical wall and can climb
            if (_tileQuery != null && (_familyState == 1 || _familyState == 2))
            {
                Vector2 posPx = (Vector2)transform.position * 100f;
                int facing = _controller.FacingDirection >= 0 ? 1 : -1;

                // Probe wall ahead
                bool wallAhead = _tileQuery.IsSolidAt(new Vector2Int((int)((posPx.x + facing * 20f) / 40f), (int)(posPx.y / 40f)));
                if (wallAhead)
                {
                    _isClimbing = true;
                    _vstorona = facing > 0 ? 1 : -1;
                    // Climb upward along the wall face
                    JumpVertical(2.5f);
                }
                else if (_isClimbing && _controller.IsGrounded)
                {
                    _isClimbing = false;
                    _vstorona = 0;
                }
            }

            // Sync visual rotation with vstorona
            Transform visual = transform.Find("Visual");
            if (visual != null)
            {
                float targetAngle = _vstorona == 1 ? -90f :
                                   _vstorona == -1 ? 90f :
                                   _vstorona == 2 ? 180f : 0f;
                visual.localEulerAngles = new Vector3(0f, 0f, targetAngle);
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
                _aiNeedLaz = UnityEngine.Random.Range(-1, 2);
                SetState(EnemyAIState.Idle);
                return;
            }

            if (!_isClimbing)
            {
                HandleLedgeAhead(tickIndex);
            }

            if (_isClimbing && _aiNeedLaz != 0)
            {
                JumpVertical(_aiNeedLaz * _patrolSpeed * 0.5f);
            }

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
            // The hunt ends on the awareness budget, not on the sighting — see
            // `EnemyBrain.ChaseBudgetExhausted`. Exiting on `TargetUnit == null` made every archetype
            // forget the player on the first obscured tick.
            if (ChaseBudgetExhausted)
            {
                SetState(EnemyAIState.Alert);
                return;
            }

            if (_blackboard.TargetUnit == null)
            {
                ChaseLastKnownPosition(tickIndex);
                return;
            }

            float dist = _blackboard.TargetDistance;
            float dx = _blackboard.TargetDeltaX;

            if (dx > 5f) ApplyFacing(1);
            else if (dx < -5f) ApplyFacing(-1);

            if (_hasRangedWeapon && dist > 150f && dist < 300f && UnityEngine.Random.value < 0.2f)
            {
                _familyState = 3; // Ranged spit
            }
            else
            {
                _familyState = 2;
            }

            // Leap attack towards target (UnitAnt optJumpAtt)
            if (dist <= _optDistAtt && _controller.IsGrounded && _jumpCooldownTicks <= 0)
            {
                JumpVertical(6f);
                MoveHorizontal((dx >= 0 ? 1f : -1f) * _chaseSpeed * 1.5f);
                _jumpCooldownTicks = 45;
            }
            else
            {
                float dir = dx >= 0 ? 1f : -1f;
                MoveHorizontal(dir * _chaseSpeed);
            }

            if (_isClimbing)
            {
                _aiNeedLaz = _blackboard.TargetDeltaY > 20f ? -1 : (_blackboard.TargetDeltaY < -20f ? 1 : 0);
                JumpVertical(_aiNeedLaz * _chaseSpeed);
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

            if (_familyState == 3 && _hasRangedWeapon)
            {
                return dist <= 300f && _blackboard.HasLineOfSight;
            }

            return dist <= _optDistAtt;
        }

        protected override void ExecuteAttackAction()
        {
            if (_controller == null || !_controller.IsAlive) return;

            if (_familyState == 3 && _hasRangedWeapon)
            {
                // Ranged spit attack (antfire)
                if (_attackTimer <= 0)
                {
                    _attackTimer = UnityEngine.Random.Range(30, 50);
                    _controller.MakeNoise(120, true);

                    float dmg = _controller.Definition != null ? _controller.Definition.damage : 10f;
                    Vector2 targetPos = (Vector2)_blackboard.TargetUnit.transform.position * 100f;

                    var pending = PendingDamage.Direct(
                        DamageContext.Contact(dmg, DamageType.Acid),
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
            }
            else
            {
                // Contact / leap attack
                _controller.TryContactAttack(_blackboard.TargetUnit, 1f);
            }
        }

        /// <summary>
        /// A port of <c>UnitAnt.animate()</c> (<c>UnitAnt.as:127-193</c>).
        ///
        /// <para><b>The death arm is gated on <c>trup</c>, and for this family it is unreachable.</b>
        /// AS3's first branch is <c>if(trup &amp;&amp; (sost == 2 || sost == 3))</c>. <c>ant</c>,
        /// <c>ant1</c>, <c>ant2</c> and <c>ant3</c> all author <c>&lt;param trup='0'/&gt;</c> and none of
        /// them authors a <c>die</c> or <c>death</c> row — which is the corroboration that the two agree,
        /// because a reachable arm would make AS3's <c>anims[animState].restart()</c> a TypeError on an
        /// unregistered id. So a dead ant falls through to the <i>live</i> branches and keeps showing
        /// <c>stay</c>/<c>walk</c>: an ant has no death pose, and its sheet has no row to play one with.
        /// This used to return <c>"die"</c>, which asked the animator for a row the sheet does not have —
        /// so a dead ant drew <b>nothing</b>.</para>
        ///
        /// <para><b>The wall branch returns <c>walk</c>/<c>stay</c>, never <c>laz</c>.</b> AS3
        /// <c>:164-174</c> is <c>else if(isLaz) { if(dy &gt; 1 || dy &lt; -1) "walk"; else "stay"; }</c> —
        /// an ant on a wall plays its walking row. <c>laz</c> is a <c>hellhound</c>/<c>raider</c> row
        /// (<c>UnitHellhound.as:660</c>, <c>UnitRaider.as:462</c>) and no ant sheet authors it, so the
        /// previous <c>"laz"</c> return was invented and blanked the sprite on every wall.</para>
        ///
        /// <para><b>Not ported, and deliberately visible here:</b> AS3's <c>else if(aiPlav || levit)</c>
        /// arm returns <c>plav</c>. The ant family authors a <c>plav</c> row, but the port has no
        /// swim/levitate flag on this brain, so that arm has no trigger to hang off. Adding it without
        /// the flag would be a return that can never be taken.</para>
        /// </summary>
        protected override string ResolveAnimState()
        {
            if (_controller == null) return null;

            // AS3 `if(trup && (sost == 2 || sost == 3))`, with the oracle's own `die`/`death` choice:
            // `stay && animState != "death"` is "grounded, and not already showing death".
            if (!_controller.IsAlive && HasReachableDeathAnimation)
            {
                return _controller.IsGrounded && !ShowingAnimState("death") ? "die" : "death";
            }

            // `else if(isLaz)` — see the remark above: `walk`/`stay`, never `laz`.
            if (_isClimbing && _controller.IsAlive)
            {
                float climbDy = _motor != null ? _motor.State.Velocity.y : 0f;
                return (climbDy > 1f || climbDy < -1f) ? "walk" : "stay";
            }

            // `else if(stay)`: dx == 0 -> stay, |dx| > 5 -> run, else walk.
            if (_controller.IsGrounded)
            {
                float spd = Mathf.Abs(_motor != null ? _motor.State.Velocity.x : 0f);
                if (spd < 0.1f) return "stay";
                return _currentState == EnemyAIState.CombatChase ? "run" : "walk";
            }

            // `else` -> jump.
            return "jump";
        }
    }
}
