using System;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Campaign;
using PFE.Systems.Combat;
using PFE.Systems.Weapons;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    public enum RobotKind
    {
        Standard = 0,   // Robobrain, Protectron, Gutsy, Sentinel
        Drone = 1,      // Dron1..3, Dront (flying 2D)
        Roller = 2,     // Roller, Roller2 (ground ram)
        Vortex = 3,     // Vortex (puller)
        SpriteBot = 4,  // SpriteBot (zap)
        Msp = 5         // Msp (stationary wake-and-fire)
    }

    /// <summary>
    /// Which subclass <c>attack()</c> this robot runs — AS3 gives <c>UnitGutsy</c>
    /// (<c>:121-146</c>) and <c>UnitSentinel</c> (<c>:119-130</c>) their own overrides, and both differ
    /// from <c>UnitAIRobot.attack()</c> in more than the secondary weapon: gutsy fires the
    /// <i>flamethrower</i> on the duty cycle and the primary only at long range, and sentinel fires the
    /// primary <b>every frame</b> plus the rocket when the target is outside 160×80.
    /// </summary>
    public enum RobotSecondaryKind
    {
        None = 0,
        Flamer = 1,     // UnitGutsy
        Rocket = 2      // UnitSentinel
    }

    /// <summary>
    /// Robot enemy brain — a high-fidelity port of AS3 <c>fe/unit/UnitAIRobot.as</c> and small robot classes
    /// (UnitDron, UnitGutsy, UnitProtect, UnitSentinel, UnitSpriteBot, UnitVortex, UnitRoller, UnitMsp).
    /// </summary>
    public class RobotBrain : EnemyBrain
    {
        [SerializeField] protected RobotKind _kind = RobotKind.Standard;

        /// <summary>
        /// Which <c>attack()</c> override this robot runs — set by <c>GutsyController</c> and
        /// <c>SentinelController</c> in <c>Awake</c>, beside <see cref="Kind"/>. Defaults to
        /// <see cref="RobotSecondaryKind.None"/>, i.e. <c>UnitAIRobot.attack()</c>.
        /// </summary>
        [SerializeField] protected RobotSecondaryKind _secondaryKind = RobotSecondaryKind.None;
        /// <summary>
        /// Burst cadence in ticks — the robot family's <c>aiAttackOch</c> (<c>UnitAIRobot.as:31</c>,
        /// declared <c>100</c>).
        ///
        /// <para><b>A constant standing in for an unimported attribute, not a tuned value.</b> AS3 takes
        /// this from the unit's own <c>&lt;un och=…&gt;</c> node (the same read as the raider's,
        /// <c>UnitRaider.as:319-321</c>), and <c>UnitExtrasParser</c> deliberately imports only
        /// <c>res</c> from that node — see its remarks. Nothing should "balance" this number; closing the
        /// <c>&lt;un&gt;</c> import is what replaces it. No shipped robot row authors <c>och</c>, so 100
        /// is also what every robot in the game actually runs at.</para>
        ///
        /// <para>It used to be <c>40</c>, which was not the oracle's number and was not measured
        /// against anything — it was the interval between two fabricated damage reports.</para>
        /// </summary>
        [SerializeField] protected float _burstCooldownTicks = 100f;
        [SerializeField] protected float _combatRangePixels = 400f;

        protected int _aiTCh;

        /// <summary>
        /// AS3 <c>aiAttackT</c> (<c>UnitAIRobot.as:29</c>, declared <c>80</c>) — the burst's own
        /// countdown. <c>attack()</c> re-arms it to <c>round((rand * 0.4 + 0.8) * och)</c> when it
        /// reaches 0 and fires while it is above <c>och * 0.25</c>, which is what makes the robot fire
        /// in bursts with a pause between them rather than at a flat interval.
        /// </summary>
        protected int _attackTimer = 80;

        protected int _familyState;
        protected bool _isDormant;

        // ── Self-steering state: SpriteBot and Vortex ─────────────────────────────────────────────
        //
        // `UnitSpriteBot.control()` (:99-175) and `UnitVortex.control()` (:101-193) REPLACE the base
        // `control()` outright — neither calls `super.control()` — so in the oracle these two units
        // never run the shared state machine at all. The port's state machine still runs (it is what
        // acquires targets, ticks the mounts and picks the animation), so their steering is applied
        // AFTER `base.SimTick` and overwrites whatever movement the state machine asked for. That is
        // the same seam `UpdateDroneHover` already uses.
        //
        // The timers are SEPARATE from `_aiTCh` on purpose. In the oracle `aiTCh` is shared, but only
        // because the base machine never runs for these units; here both would decrement one field
        // and the steering would re-roll at the state machine's cadence instead of its own.
        //
        // Y IS NEGATED EVERYWHERE BELOW. AS3's Y runs down and the port's runs up, and every
        // direction in these two routines is a Y direction. The steering math is kept in AS3's
        // convention (down-positive) so it can be read against the oracle line by line, and the flip
        // happens exactly once, at the call to `SetVerticalSpeed`.
        private int _steerTimer;
        private float _steerDx, _steerDy;      // AS3 aiDx / aiDy, Y down-positive
        private float _steerDistance;          // AS3 aiRasst
        private float _steerSpeed;             // AS3 maxSpeed for the current roll
        private float _spriteWalkSpeed, _spriteRunSpeed;
        private bool _spriteSpeedsRolled;

        private int _vortexTimer;
        private int _vortexSpok;               // AS3 aiSpok (Unit.as:360, declared 0)
        private int _vortexSteerState;         // AS3 aiState, local to control(): 0 idle, 1 chase, 2 spool
        private float _vortexCelX, _vortexCelY;   // AS3 celX / celY, Y down-positive
        private float _vortexSpdX, _vortexSpdY;   // AS3 spd.x / spd.y
        private float _vortexWalkSpeed, _vortexRunSpeed;
        private bool _vortexSpeedsReady;

        /// <summary>AS3 <c>Unit.maxSpok</c> (<c>Unit.as:362</c>, declared <c>30</c>).</summary>
        private const int VortexMaxSpok = 30;


        public RobotKind Kind { get => _kind; set => _kind = value; }
        public RobotSecondaryKind SecondaryKind { get => _secondaryKind; set => _secondaryKind = value; }
        public int FamilyState => _familyState;

        /// <summary>
        /// This unit as a <see cref="RobotController"/> — the holder of the rolled weapon and its mount.
        /// <c>_controller</c> is typed <c>UnitController</c> on <see cref="EnemyBrain"/>, so the weapon
        /// half needs the cast; null means the brain is attached to something that is not a robot.
        /// </summary>
        protected RobotController RobotCtrl => _controller as RobotController;

        protected override bool StopsToAttack => _kind == RobotKind.Standard;

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

            float spd = def.moveSpeed > 0 ? def.moveSpeed : 2f;
            _patrolSpeed = spd;
            _chaseSpeed = _kind == RobotKind.Roller ? spd * 2.5f : spd * 1.8f;
            _alertSpeed = spd * 1.2f;

            if (def.detectionDistance > 0)
            {
                _combatRangePixels = def.detectionDistance;
            }

            if (_kind == RobotKind.Msp)
            {
                _isDormant = true;
            }
        }

        /// <summary>
        /// Robot network Budilo: reaches robot brains by pure distance without the receiver ear multiplier,
        /// ensuring ear=0 robots (spritebot, vortex, roller) receive alarms (02_SHARED_SYSTEMS.md §3).
        /// </summary>
        protected override void Budilo(float radius = 500f)
        {
            // AS3 `Unit.budilo` first line is `this.makeNoise(this.noiseRun * 1.2)`
            // (Unit.as:4276) — the unit's OWN run noise, not the 200 default. Robots author
            // `<vis noise='500'/>`, so the shout carries 600 px, not 240. Using the default here
            // made a robot's alarm call quieter than its own footsteps.
            if (_controller != null)
            {
                _controller.MakeNoise(Mathf.RoundToInt(_controller.NoiseRun * AlarmNoiseMultiplier), true);
            }

            var hits = Physics2D.OverlapCircleAll(transform.position, radius * TileQueryConstants.PixelToUnit);
            foreach (var hit in hits)
            {
                if (hit != null && hit.TryGetComponent(out RobotController rc) && rc.Brain != null && rc.IsAlive)
                {
                    rc.Brain.RaiseRobotAlarm((Vector2)transform.position * 100f);
                }
            }
        }

        public void RaiseRobotAlarm(Vector2 noisePos)
        {
            _isDormant = false;
            _blackboard.HasHeardNoise = true;
            _blackboard.LastHeardNoisePosition = noisePos;
            _blackboard.AlertTimerTicks = 60;
            SetState(EnemyAIState.Alert);
        }

        public override void SimTick(int tickIndex)
        {
            if (_aiTCh > 0) _aiTCh--;

            // Release last tick's trigger before the state machine runs, exactly as the ArmedShooter
            // brain does and for the same reason: `EnemyWeaponMount` latches `_firing` until it is told
            // otherwise, so a robot that loses its target would keep shooting while it walks away.
            // `ExecuteAttackAction` re-arms this within the same tick, before `base.SimTick` ends in
            // `TickWeaponMounts`.
            SetWeaponTrigger(false);
            ReleaseSecondaryTrigger();

            base.SimTick(tickIndex);

            if (_kind == RobotKind.Drone && _controller != null && _controller.IsAlive)
            {
                UpdateDroneHover(tickIndex);
            }

            // SpriteBot and Vortex steer themselves. Guarded on alive and on the same freeze the base
            // tick honours: both oracle routines open with `if(sost >= 3) return;` and
            // `if(World.w.enemyAct <= 0) return;`, so a corpse must not drift and a frozen enemy must
            // not wander.
            if (_controller != null && _controller.IsAlive && !EnemyActivity.IsFrozen)
            {
                if (_kind == RobotKind.SpriteBot)
                {
                    UpdateSpriteBotSteering();
                }
                else if (_kind == RobotKind.Vortex)
                {
                    UpdateVortexSteering();
                }
            }
        }

        /// <summary>
        /// AS3 <c>UnitSpriteBot.control()</c> (<c>UnitSpriteBot.as:96-176</c>) — the erratic 2-D
        /// wander that makes a spritebot dart instead of hover.
        ///
        /// <para><b>The shape.</b> A roll happens only when <c>aiTCh</c> expires; between rolls the
        /// unit coasts on the velocity that roll produced. Each roll picks a speed and a heading:</para>
        /// <list type="bullet">
        /// <item><description>with a target: <c>runSpeed * (rand*0.6 + 0.7)</c> — 0.7× to 1.3× run —
        /// and a re-roll every 20-69 ticks;</description></item>
        /// <item><description>without one: <c>walkSpeed * (rand + 0.5)</c> — 0.5× to 1.5× walk — and a
        /// re-roll every 30-179 ticks.</description></item>
        /// </list>
        ///
        /// <para><b>Then the heading, and the counter-intuitive part.</b> <c>aiRasst</c> is set to the
        /// distance to the target when there is one and to <b>0</b> when there is not, and the branch
        /// below tests <c>aiRasst &gt; 500</c>. So a spritebot only walks straight at its target when
        /// that target is <i>more than 500 px away</i>; inside 500 px it takes a <b>random heading</b>
        /// at run speed. That is the whole character of the unit — it orbits and jitters around the
        /// player rather than closing — and it is easy to "fix" by accident into a straight chase.
        /// The <c>setCel(X + dx*10, Y + dy*10)</c> in the no-target arm looks like a heading but is
        /// dead for direction purposes: it runs in the same block that just set <c>aiRasst = 0</c>, so
        /// the random-heading branch always wins there.</para>
        ///
        /// <para><b>Divergence, recorded.</b> The oracle writes <c>dx</c>/<c>dy</c> once per roll and
        /// lets <c>forces()</c> damp them by 0.95 per frame, so the unit decelerates between rolls.
        /// The port commands a <i>desired</i> velocity every step (the motor has no "coast"), so the
        /// coast-and-damp is not reproduced: speed stays at the rolled value until the next roll.</para>
        /// </summary>
        private void UpdateSpriteBotSteering()
        {
            EnsureSpriteSpeeds();

            bool hasTarget = _blackboard.TargetUnit != null;

            if (_steerTimer > 0)
            {
                _steerTimer--;
            }
            else
            {
                if (hasTarget)
                {
                    _steerSpeed = _spriteRunSpeed * (UnityEngine.Random.value * 0.6f + 0.7f);
                    // AS3 `aiRasst = sqrt(celDX*celDX + celDY*celDY)` — derived from the same deltas
                    // the heading is normalised by, not read from a separate distance field. Deriving
                    // it here guarantees the vector below is unit length even if a cached distance and
                    // the deltas ever disagree.
                    _steerDistance = Mathf.Sqrt(
                        _blackboard.TargetDeltaX * _blackboard.TargetDeltaX +
                        _blackboard.TargetDeltaY * _blackboard.TargetDeltaY);
                    // floor(rand*50) + 20
                    _steerTimer = UnityEngine.Random.Range(20, 70);
                }
                else
                {
                    _steerSpeed = _spriteWalkSpeed * (UnityEngine.Random.value + 0.5f);
                    _steerDistance = 0f;
                    // floor(rand*150) + 30
                    _steerTimer = UnityEngine.Random.Range(30, 180);
                }

                if (_steerDistance > 500f)
                {
                    // celDX / celDY normalised by the distance. `TargetDeltaY` is up-positive, so the
                    // negation puts it back in AS3's down-positive frame.
                    _steerDx = _blackboard.TargetDeltaX / _steerDistance;
                    _steerDy = -_blackboard.TargetDeltaY / _steerDistance;
                }
                else
                {
                    float angle = UnityEngine.Random.value * Mathf.PI * 2f;   // Math.random() * 360 degrees
                    _steerDx = Mathf.Sin(angle);
                    _steerDy = Mathf.Cos(angle);
                }
            }

            ApplyFacing(_steerDx > 0f ? 1 : -1);

            MoveHorizontal(_steerDx * _steerSpeed);
            // The one place the mirror is undone: AS3's down-positive dy becomes Unity's up-positive Y.
            SetVerticalSpeed(-_steerDy * _steerSpeed);
        }

        /// <summary>
        /// AS3 <c>UnitSpriteBot.as:24-26</c> — <c>maxSpeed += Math.random()*2 - 1; walkSpeed =
        /// maxSpeed; runSpeed = maxSpeed * 1.5;</c>.
        ///
        /// <para><b>Rolled once per spawn, and that is load-bearing.</b> The ±1 px/frame jitter is
        /// what stops two spritebots spawned together from moving in lockstep. Rolling it per tick
        /// would average it away; rolling it in <see cref="ApplyDefinitionTuning"/> would re-roll it
        /// every time a definition is re-assigned, which happens more than once per spawn.</para>
        /// </summary>
        private void EnsureSpriteSpeeds()
        {
            if (_spriteSpeedsRolled)
            {
                return;
            }

            float spd = (_controller != null && _controller.Stats != null && _controller.Stats.moveSpeed > 0f)
                ? _controller.Stats.moveSpeed
                : 2f;

            float baseSpeed = spd + UnityEngine.Random.value * 2f - 1f;
            _spriteWalkSpeed = baseSpeed;
            _spriteRunSpeed = baseSpeed * 1.5f;
            _spriteSpeedsRolled = true;
        }

        /// <summary>
        /// AS3 <c>UnitVortex.control()</c> (<c>UnitVortex.as:101-193</c>) — the vortex wanders on a
        /// three-state <c>aiSpok</c> charge meter rather than on a sighting.
        ///
        /// <para><b>The meter, not the player, picks the state.</b> Every 100-199 ticks
        /// <c>aiState</c> is re-derived from <c>aiSpok</c>: <c>0</c> → idle (state 0), <c>&gt;=
        /// maxSpok</c> (30) → aggressive (state 1), anything between → spool (state 2). Only the
        /// aggressive state chases and only it attacks.</para>
        ///
        /// <para><b>What fills the meter.</b> Every 10th tick, if <c>findCel()</c> commits a target,
        /// <c>aiSpok</c> jumps to <c>maxSpok + 10</c> — i.e. one sighting pins it at aggressive for a
        /// long time. Without a target it bleeds one point per 10 ticks. That is why a vortex that has
        /// seen the player keeps charging after losing sight of them.</para>
        ///
        /// <para><b>Idle movement is a leap, not a nudge.</b> In state 0 the target cell is re-picked
        /// 400-700 px to one side and ±100 px vertically — half the time it instead picks its own
        /// mid-body and simply hangs there. The unit then <i>accelerates</i> toward it: the offset is
        /// clamped to <c>accel</c> (<c>accel/2</c> when idle) and accumulated into the velocity, so a
        /// vortex drifts and turns instead of snapping to a direction.</para>
        ///
        /// <para><b>Three oracle quirks kept as-is.</b> (1) The facing test is <c>storona = celX &gt; X
        /// ? 1 : -1</c> — it faces the <i>wander target</i>, not the player, so a charging vortex can
        /// visually face away from what it is about to hit. (2) <c>if(rasst2 &lt; 800*800) { }</c> at
        /// the top of <c>control()</c> is an empty block: the distance is computed and discarded.
        /// (3) <b><c>celX</c>/<c>celY</c> are assigned only inside <c>if(aiState == 0)</c></b>, so once
        /// a sighting pins the state to 1 the destination is <i>frozen at the last idle point</i> and
        /// the vortex keeps accelerating toward a stale coordinate. It never actually steers at the
        /// player — it only lands damage through <c>attKorp</c> when the player happens to be close.
        /// That reads like a bug and may be one, but it is what ships.</para>
        ///
        /// <para><b>Divergence, recorded.</b> The oracle writes <c>dx</c>/<c>dy</c> and damps them
        /// 0.95/frame in <c>forces()</c> once they exceed <c>maxSpeed</c>; the port's
        /// <c>SetDesired*Speed</c> assigns the velocity outright, so the physics brake never sees these
        /// values and the same <c>if(|v|² &gt; maxSpeed²) v *= 0.95</c> is applied here instead. The
        /// steady-state speed matches; the approach curve is shorter.</para>
        /// </summary>
        private void UpdateVortexSteering()
        {
            EnsureVortexSpeeds();

            // Work in AS3's frame throughout: pixels, Y down-positive. One conversion, at the top.
            Vector2 posPx = (Vector2)_controller.transform.position * TileQueryConstants.UnitToPixel;
            float selfX = posPx.x;
            float selfY = -posPx.y;

            float heightPx = (_controller.Stats != null ? _controller.Stats.Height : 0.7f)
                             * TileQueryConstants.UnitToPixel;
            float midY = selfY - heightPx * 0.5f;   // AS3 `Y - scY/2`

            if (_vortexTimer > 0)
            {
                _vortexTimer--;
            }
            else
            {
                if (_vortexSpok == 0)
                {
                    _vortexSteerState = 0;
                }
                else if (_vortexSpok >= VortexMaxSpok)
                {
                    _vortexSteerState = 1;
                }
                else
                {
                    _vortexSteerState = 2;
                }

                // floor(rand*100) + 100
                _vortexTimer = UnityEngine.Random.Range(100, 200);

                if (_vortexSteerState == 0)
                {
                    if (UnityEngine.Random.value < 0.5f)   // isrnd()
                    {
                        float side = UnityEngine.Random.value < 0.5f ? 1f : -1f;
                        _vortexCelX = selfX + (UnityEngine.Random.value * 300f + 400f) * side;
                        _vortexCelY = selfY + UnityEngine.Random.value * 200f - 100f;
                    }
                    else
                    {
                        _vortexCelX = selfX;
                        _vortexCelY = midY;
                    }
                }
            }

            // One block, one condition, as in the oracle (`:151-166`): the meter update and the
            // acceleration impulse are the same event, and the velocity is nudged on one tick in ten
            // and coasts on the other nine.
            if (_vortexTimer % 10 == 1)
            {
                if (_blackboard.TargetUnit != null)   // findCel() committed
                {
                    _vortexSpok = VortexMaxSpok + 10;
                    _vortexSteerState = 1;
                }
                else if (_vortexSpok > 0)
                {
                    _vortexSpok--;
                }

                // Read AFTER the state update above, because the oracle's `norma(spd, aiState == 0 ?
                // accel/2 : accel)` is evaluated after its own `aiState = 1` assignment — a sighting
                // therefore accelerates at the full `accel` on the very tick it is made.
                float accel = (_controller.Stats != null ? _controller.Stats.Acceleration : 1.5f);
                float step = _vortexSteerState == 0 ? accel * 0.5f : accel;

                // AS3 `spd.x`/`spd.y` is a TRANSIENT offset, distinct from `dx`/`dy`, the unit's
                // accumulated velocity. Keeping them apart matters: this is an acceleration impulse,
                // not a new velocity.
                float spdX = _vortexCelX - selfX;
                float spdY = _vortexCelY - midY;
                ClampLength(ref spdX, ref spdY, step);   // AS3 `norma(spd, …)`

                // `dx += spd.x; dy += spd.y;`
                _vortexSpdX += spdX;
                _vortexSpdY += spdY;
            }

            // Also after the block: `maxSpeed = aiState == 0 ? walkSpeed : runSpeed` sits below it in
            // the oracle too, so the tick that promotes the vortex to aggressive already uses runSpeed.
            float maxSpeed = _vortexSteerState == 0 ? _vortexWalkSpeed : _vortexRunSpeed;

            // AS3 `UnitVortex.forces()` (:42-50) — the flyer arm damps only once the speed is OVER the
            // state's ceiling: `if(dx*dx+dy*dy > maxSpeed*maxSpeed) { dx*=0.95; dy*=0.95; }`. It is not
            // an unconditional brake, and reproducing it here is required rather than optional: the
            // port's `SetDesired*Speed` assigns the velocity outright, so the physics brake that would
            // otherwise do this never sees these values.
            float speedSq = _vortexSpdX * _vortexSpdX + _vortexSpdY * _vortexSpdY;
            if (speedSq > maxSpeed * maxSpeed)
            {
                _vortexSpdX *= 0.95f;
                _vortexSpdY *= 0.95f;
            }

            ApplyFacing(_vortexCelX > selfX ? 1 : -1);

            MoveHorizontal(_vortexSpdX);
            SetVerticalSpeed(-_vortexSpdY);
        }

        /// <summary>AS3 <c>Obj.norma</c> (<c>Obj.as:248-256</c>) — clamp a vector to a maximum length.</summary>
        private static void ClampLength(ref float x, ref float y, float maxLength)
        {
            float sq = x * x + y * y;
            if (sq <= maxLength * maxLength)
            {
                return;
            }

            float length = Mathf.Sqrt(sq);
            if (length <= 0f)
            {
                return;
            }

            x *= maxLength / length;
            y *= maxLength / length;
        }

        /// <summary>
        /// AS3 <c>UnitVortex.as:22-23</c> — <c>walkSpeed = maxSpeed; runSpeed = maxSpeed * 3;</c>.
        /// The port's generic <c>_chaseSpeed</c> is <c>spd * 1.8</c>, which is neither this unit's
        /// ratio nor the spritebot's 1.5, so both are derived from their own rows.
        /// </summary>
        private void EnsureVortexSpeeds()
        {
            if (_vortexSpeedsReady)
            {
                return;
            }

            float spd = (_controller != null && _controller.Stats != null && _controller.Stats.moveSpeed > 0f)
                ? _controller.Stats.moveSpeed
                : 4f;

            _vortexWalkSpeed = spd;
            _vortexRunSpeed = spd * 3f;
            _vortexSpeedsReady = true;
        }

        protected virtual void UpdateDroneHover(int tickIndex)
        {
            // Drone 2D altitude adjustment
            if (_blackboard.HasLastKnownTargetPosition)
            {
                float targetDy = _blackboard.TargetDeltaY;
                float desiredY = targetDy + 50f;
                if (desiredY > 15f) JumpVertical(2f);
                else if (desiredY < -15f) JumpVertical(-1.5f);
            }
            else
            {
                float wave = Mathf.Sin(tickIndex * 0.15f) * 1.2f;
                JumpVertical(wave);
            }
        }

        protected override void TickIdle(int tickIndex)
        {
            _familyState = 0;
            if (_isDormant)
            {
                StopMovement();
                if (_blackboard.HasLineOfSight && _blackboard.TargetUnit != null)
                {
                    _isDormant = false;
                    _familyState = 4; // wake state
                    _aiTCh = 25;
                }
                return;
            }

            if (_aiTCh <= 0)
            {
                _aiTCh = UnityEngine.Random.Range(40, 90);
                SetState(EnemyAIState.Patrol);
                return;
            }

            if (_blackboard.HasLineOfSight && _blackboard.TargetUnit != null)
            {
                _familyState = 5; // spotted pause
                _aiTCh = UnityEngine.Random.Range(20, 50);
                SetState(EnemyAIState.Alert);
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
                _familyState = 5;
                _aiTCh = UnityEngine.Random.Range(20, 50);
                SetState(EnemyAIState.Alert);
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
            if (_familyState == 5)
            {
                StopMovement();
                if (_aiTCh <= 0)
                {
                    // The oracle's test here is `if(celUnit)` (`UnitRaider.as:698-715`), and `celUnit`
                    // is the unit this brain has COMMITTED to — not merely "a target object exists".
                    // The port's `TargetUnit` can be a stale reference: `EnemySensors` keeps it while
                    // `HasLineOfSight` is true even after the observation meter has drained below the
                    // chase threshold (`EnemySensors.cs:758-772`). Promoting on `!= null` alone sent
                    // the unit into `CombatChase` only for it to bail straight back on
                    // `ChaseBudgetExhausted`, and it did so on a `_aiTCh` period — a slower version of
                    // the per-tick `Alert`/`CombatChase` ping-pong fixed in `EnemyBrain.TickAlert`.
                    // The `else` arm below is the oracle's `else { aiState = 1; }`, so a stale target
                    // demotes to patrol rather than freezing in the deciding state.
                    if (_blackboard.TargetUnit != null && !ChaseBudgetExhausted)
                    {
                        _familyState = 3;
                        SetState(EnemyAIState.CombatChase);
                    }
                    else
                    {
                        _familyState = 1;
                        SetState(EnemyAIState.Patrol);
                    }
                }
                return;
            }

            _familyState = 2;
            base.TickAlert(tickIndex);
        }

        protected override void TickCombatChase(int tickIndex)
        {
            _familyState = 3;

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

            if (_kind == RobotKind.Roller)
            {
                // Charge straight at target to ram
                float dir = dx >= 0 ? 1f : -1f;
                MoveHorizontal(dir * _chaseSpeed);
            }
            else if (_kind == RobotKind.Vortex)
            {
                // Superseded by UpdateVortexSteering, which runs after base.SimTick and overwrites
                // whatever this arm commands. Kept rather than deleted because the state machine still
                // has to leave the vortex in a sane state on the ticks its own steering is skipped
                // (frozen), and because the oracle's chase decision is aiState == 1, not proximity.
                // Pull target toward vortex
                if (dist > 120f)
                {
                    float dir = dx >= 0 ? 1f : -1f;
                    MoveHorizontal(dir * _patrolSpeed);
                }
                else
                {
                    StopMovement();
                }
            }
            else if (_kind == RobotKind.Msp)
            {
                // Stationary turret-like
                StopMovement();
            }
            else
            {
                // Standard & Drone stand-off firing
                if (dist > _combatRangePixels * 0.7f)
                {
                    float dir = dx >= 0 ? 1f : -1f;
                    MoveHorizontal(dir * _chaseSpeed);
                }
                else if (dist < 100f)
                {
                    float dir = dx >= 0 ? -1f : 1f;
                    MoveHorizontal(dir * _patrolSpeed);
                }
                else
                {
                    StopMovement();
                }
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

            if (_kind == RobotKind.Roller)
            {
                return dist <= 100f && Mathf.Abs(_blackboard.TargetDeltaY) <= 50f;
            }
            return dist <= _combatRangePixels && _blackboard.HasLineOfSight;
        }

        /// <summary>
        /// AS3 <c>UnitAIRobot.attack()</c> (<c>UnitAIRobot.as:583-601</c>) — hold the trigger for this
        /// tick on the oracle's burst cadence, instead of fabricating one damage report per cooldown.
        /// </summary>
        /// <remarks>
        /// <para><b>What the oracle does, verbatim:</b></para>
        /// <code>
        /// if(aiAttackOch == 0 &amp;&amp; shok &lt;= 0 &amp;&amp; isrnd(0.1)) currentWeapon.attack();
        /// if(aiAttackOch &gt; 0) {
        ///    if(aiAttackT &lt;= 0) aiAttackT = Math.round((Math.random() * 0.4 + 0.8) * aiAttackOch);
        ///    if(aiAttackT &gt; aiAttackOch * 0.25) currentWeapon.attack();
        ///    --aiAttackT;
        /// }
        /// </code>
        /// <para>The port's equivalent of <c>currentWeapon.attack()</c> is
        /// <see cref="SetWeaponTrigger"/>, the same seam the ArmedShooter family uses: the weapon's own
        /// <c>t_attack</c> paces the individual rounds, so this method only decides whether the trigger
        /// is down <i>this</i> tick.</para>
        ///
        /// <para><b>The direct-damage body is kept as the unarmed fallback, not as the normal path.</b>
        /// It now runs only for a robot with no mount: the five contact robots (<c>spritebot</c>,
        /// <c>vortex</c>, <c>roller</c>, <c>roller2</c>, <c>msp</c>, whose rows author no
        /// <c>&lt;w&gt;</c>), a row whose candidates all failed the difficulty gate, or a bare test spawn
        /// with no weapon services. Deleting it would make those units harmless; leaving it as the
        /// primary path is what made every armed robot harmless <i>as a shooter</i> — a robobrain hit for
        /// its row's generic <c>damage</c> with no bolt, no flare and no report.</para>
        /// </remarks>
        protected override void ExecuteAttackAction()
        {
            if (_controller == null || !_controller.IsAlive) return;

            if (_kind == RobotKind.Roller)
            {
                // Contact ram attack — `UnitRoller.attack()` is a body hit, not a weapon.
                _controller.TryContactAttack(_blackboard.TargetUnit, 1.2f);
                return;
            }

            // The two subclasses that override `attack()` outright. Both are checked BEFORE the generic
            // path because they replace it rather than extend it — gutsy never runs the plain duty
            // cycle, and sentinel never runs a cadence at all.
            if (_secondaryKind == RobotSecondaryKind.Flamer)
            {
                ExecuteGutsyAttack();
                return;
            }

            if (_secondaryKind == RobotSecondaryKind.Rocket)
            {
                ExecuteSentinelAttack();
                return;
            }

            // No noise here. The oracle makes a robot audible exactly twice per shot cycle, and
            // neither happens at the decision point:
            //   * the SHOT  — `Weapon.as:1579` `owner.makeNoise(this.noise, true)`, from the
            //                 weapon's own `<snd noise>`, which `ProjectileSpawner.AnnounceShot`
            //                 already emits from the ShotPlan (see ShotCues.NoiseRadius);
            //   * the CRASH — `Unit.as:3284`, same value, when the round hits a wall.
            // The old `MakeNoise(250, true)` here ran BEFORE the cooldown gate, so it charged the
            // robot once per decision tick — noise with no shot behind it, at an invented radius.
            // grep for `makeNoise` across the oracle's units returns zero hits in any brain: the
            // complete caller set is movement/landing (Unit.as:3083-3099, :2360-2368), budilo
            // (:4276), the shot (Weapon.as:1581), the wall crash (:3284) and doors
            // (Interact.as:1312). An archetype-level fire noise is not in that set.

            RobotController robot = RobotCtrl;
            EnemyWeaponMount mount = robot != null ? robot.WeaponMount : null;

            if (mount != null)
            {
                float och = _burstCooldownTicks;

                if (och <= 0f)
                {
                    // "No cooldown" weapons fire on a flat 10% chance per tick — and this is the ONLY
                    // arm the oracle guards with `shok <= 0` (`UnitAIRobot.as:585`:
                    // `if(this.aiAttackOch == 0 && shok <= 0 && isrnd(0.1))`). The `aiAttackOch > 0`
                    // arm below carries no such test, so a stunned robot with a cadence weapon keeps
                    // firing in the original. Copying that asymmetry rather than "fixing" it: a robot
                    // runs at `aiAttackOch = 100` (`:26`) and never reaches this line at all, which is
                    // why the difference is invisible until a weapon authors `och='0'`.
                    if (_blackboard.ShockTimerTicks > 0) return;

                    if (UnityEngine.Random.value < 0.1f) SetWeaponTrigger(true);
                    return;
                }

                if (_attackTimer <= 0)
                {
                    _attackTimer = Mathf.RoundToInt((UnityEngine.Random.value * 0.4f + 0.8f) * och);
                }

                if (_attackTimer > och * 0.25f) SetWeaponTrigger(true);

                _attackTimer--;
                return;
            }

            // ── Unarmed fallback ────────────────────────────────────────────────────────────────────
            float baseDamage = _controller.Definition != null ? _controller.Definition.damage : 12f;
            DamageType dType = _controller.Definition != null ? _controller.Definition.damageType : DamageType.Laser;

            Vector2 targetPos = (Vector2)_blackboard.TargetUnit.transform.position * 100f;

            var pending = PendingDamage.Direct(
                DamageContext.Contact(baseDamage, dType),
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

        /// <summary>
        /// Hold or release the rolled weapon's trigger, aiming it at the target while it fires.
        ///
        /// <para><b>The aim is in Unity world units and must not be scaled.</b> <c>TargetDeltaX/Y</c>
        /// really are pixels, but they are the AI's range units; <c>WeaponHoldPointMath</c> and
        /// <c>RangedWeaponController</c> both resolve in world units, and a <c>* 100f</c> here is the
        /// documented cause of the alicorn's "fires at 45° to the right" bug.</para>
        /// </summary>
        protected virtual void SetWeaponTrigger(bool firing)
        {
            RobotController robot = RobotCtrl;
            if (robot == null) return;

            EnemyWeaponMount mount = robot.WeaponMount;
            if (mount == null) return;

            if (firing && _blackboard.TargetUnit != null)
            {
                mount.SetAimTarget(_blackboard.TargetUnit.transform.position);
            }

            mount.SetFiring(firing);
        }

        /// <summary>
        /// <c>UnitGutsy.attack()</c> (<c>UnitGutsy.as:121-146</c>) — the flamethrower is the weapon it
        /// fights with, and the plasma is only a long-range fallback.
        /// </summary>
        /// <remarks>
        /// <code>
        /// if(celDX &lt; 100 &amp;&amp; celDX &gt; -100 &amp;&amp; celDY &lt; 80 &amp;&amp; celDY &gt; -80 &amp;&amp; celUnit) attKorp(celUnit,1);
        /// if(celDX &lt; 300 &amp;&amp; celDX &gt; -300 &amp;&amp; celDY &lt; 300 &amp;&amp; celDY &gt; -300 || aiAttackT &gt; 0) {
        ///    if(aiAttackOch &gt; 0) {
        ///       if(aiAttackT &lt;= 0) aiAttackT = Math.round((random*0.4 + 0.8) * aiAttackOch);
        ///       if(aiAttackT &gt; aiAttackOch * 0.25) this.dopWeapon.attack();
        ///       --aiAttackT;
        ///    }
        /// }
        /// else if(isrnd(0.1)) currentWeapon.attack();
        /// </code>
        /// <para><b>Note what is NOT here:</b> the base class's <c>aiAttackOch == 0</c> arm. Gutsy's
        /// whole duty cycle is inside <c>if(aiAttackOch &gt; 0)</c>, so a gutsy with an <c>och</c> of 0
        /// simply never fires the flamethrower — it falls through to the 10 % plasma roll.</para>
        /// </remarks>
        protected virtual void ExecuteGutsyAttack()
        {
            Vector2 d = TargetDelta();

            // AS3 spells the null test out: `… && Boolean(celUnit)`. `TargetDelta()` is zero when there
            // is no target, and zero is inside every box, so omitting the test would make an unarmed
            // gutsy bite at the origin.
            if (_blackboard.TargetUnit != null && Mathf.Abs(d.x) < 100f && Mathf.Abs(d.y) < 80f)
            {
                _controller.TryContactAttack(_blackboard.TargetUnit, 1f);
            }

            float och = _burstCooldownTicks;
            bool inBox = Mathf.Abs(d.x) < 300f && Mathf.Abs(d.y) < 300f;

            // The oracle's shape, including the arm that does NOTHING: when `aiAttackOch == 0` the
            // outer `if` is still entered but its body is skipped, so the `else if` plasma roll is
            // unreachable while the target is inside the box. Folding `och > 0` into the outer
            // condition instead would let an `och`-less gutsy spray plasma at point-blank range.
            if (inBox || _attackTimer > 0)
            {
                if (och > 0f)
                {
                    if (_attackTimer <= 0)
                    {
                        _attackTimer = Mathf.RoundToInt((UnityEngine.Random.value * 0.4f + 0.8f) * och);
                    }

                    if (_attackTimer > och * 0.25f) SetSecondaryTrigger(true);

                    _attackTimer--;
                }

                return;
            }

            if (UnityEngine.Random.value < 0.1f) SetWeaponTrigger(true);
        }

        /// <summary>
        /// <c>UnitSentinel.attack()</c> (<c>UnitSentinel.as:119-130</c>) — the primary fires every
        /// frame and the rocket only when the target is outside 160×80.
        /// </summary>
        /// <remarks>
        /// <code>
        /// if(celDX &lt; 100 &amp;&amp; celDX &gt; -100 &amp;&amp; celDY &lt; 80 &amp;&amp; celDY &gt; -80 &amp;&amp; celUnit) attKorp(celUnit,1);
        /// currentWeapon.attack();
        /// if(celUnit &amp;&amp; !(celDX &lt; 160 &amp;&amp; celDX &gt; -160 &amp;&amp; celDY &lt; 80 &amp;&amp; celDY &gt; -80)) this.dopWeapon.attack();
        /// </code>
        /// <para><b><c>currentWeapon.attack()</c> with no cadence is correct, not a bug:</b> the weapon's
        /// own <c>rapid</c> paces the rounds, and holding the trigger every frame is exactly what the
        /// player's weapon does. A sentinel's gatling is therefore gated by its 2-frame <c>rapid</c>, not
        /// by the brain.</para>
        /// </remarks>
        protected virtual void ExecuteSentinelAttack()
        {
            Vector2 d = TargetDelta();

            if (_blackboard.TargetUnit != null && Mathf.Abs(d.x) < 100f && Mathf.Abs(d.y) < 80f)
            {
                _controller.TryContactAttack(_blackboard.TargetUnit, 1f);
            }

            SetWeaponTrigger(true);

            // `Boolean(celUnit) && !(…)` — the rocket needs a target AND distance. Without the null
            // test, `TargetDelta()`'s zero vector reads as point-blank and the sentinel never launches.
            if (_blackboard.TargetUnit != null
                && !(Mathf.Abs(d.x) < 160f && Mathf.Abs(d.y) < 80f))
            {
                SetSecondaryTrigger(true);
            }
        }

        /// <summary>Target delta in pixels, or zero when there is no target.</summary>
        private Vector2 TargetDelta()
            => _blackboard.TargetUnit != null
                ? new Vector2(_blackboard.TargetDeltaX, _blackboard.TargetDeltaY)
                : Vector2.zero;

        /// <summary>Hold or release the secondary weapon's trigger, aiming it while it fires.</summary>
        protected virtual void SetSecondaryTrigger(bool firing)
        {
            RobotController robot = RobotCtrl;
            if (robot == null) return;

            EnemyWeaponMount mount = robot.SecondaryMount;
            if (mount == null) return;

            if (firing && _blackboard.TargetUnit != null)
            {
                mount.SetAimTarget(_blackboard.TargetUnit.transform.position);
            }

            mount.SetFiring(firing);
        }

        /// <summary>
        /// Release last tick's secondary trigger, beside <see cref="SetWeaponTrigger"/>'s release.
        /// A robot with no secondary pays one null check.
        /// </summary>
        protected virtual void ReleaseSecondaryTrigger()
        {
            RobotCtrl?.SecondaryMount?.SetFiring(false);
        }

        protected override string ResolveAnimState()
        {
            if (_controller == null) return null;

            if (!_controller.IsAlive)
            {
                return _controller.IsGrounded ? "fall" : "die";
            }

            if (_isDormant)
            {
                return "stay";
            }

            if (_kind == RobotKind.Drone)
            {
                return "fly";
            }

            if (_kind == RobotKind.Msp && _familyState == 4)
            {
                return "stay";
            }

            if (_controller.IsGrounded)
            {
                float spd = Mathf.Abs(_motor != null ? _motor.State.Velocity.x : 0f);
                if (spd < 0.1f) return "stay";
                return _currentState == EnemyAIState.CombatChase ? "run" : "walk";
            }

            return "stay";
        }
    }
}
