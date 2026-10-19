using System;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    /// <summary>
    /// Melee undead brain — a port of the reachable half of AS3 <c>fe/unit/UnitZombie.as</c>.
    ///
    /// <para><b>What it is a port of.</b> The zombie has no dedicated AI method: its per-tick brain is
    /// <c>UnitZombie.control()</c> (<c>UnitZombie.as:533-953</c>), which the base class calls in place of
    /// its own. Three things in there decide what the player sees, and all three are ported:
    /// the <b>speed ladder</b> (<c>:709-714</c> — walk, then <c>runSpeed * 0.6</c> when alerted, then
    /// <c>runSpeed</c> when chasing), the <b>contact attack</b> (<c>:938-950</c> — an AABB overlap, not a
    /// radius, rate-limited by the <i>target's</i> invulnerability window), and the <b>animation
    /// state</b> (<c>animate()</c>, <c>:215-320</c>).</para>
    ///
    /// <para><b>What is modelled beyond the three above.</b> The <b>digger</b> — the buried ambush —
    /// is ported: <c>zakop</c>/<c>vykop</c> (<c>:414-464</c>), the bury gate in <c>setPos</c>
    /// (<c>:192-213</c>), the wake rules in <c>control()</c> (<c>:574-606</c>) and the <c>alarma()</c>
    /// override that promotes a buried unit to digging (<c>:328-349</c>). It is the reason a zombie can
    /// be invisible for a while and then climb out of the floor: the roll is per placement
    /// (<c>:120-127</c>), so a room's zombies are a mix of walkers and ambushers. See
    /// <see cref="PFE.Entities.Enemies.EnemyAIState.Buried"/>.</para>
    ///
    /// <para><b>The super system is ported</b> — <c>setSuper()</c>/<c>findSuper()</c>/<c>superSila()</c>/
    /// <c>superSila2()</c>/<c>superSilaVse()</c> (<c>:954-1228</c>), driven by
    /// <c>&lt;un ss='N'/&gt;</c> (<see cref="UnitDefinition.superSkill"/>). Nine variants carry it and
    /// each is a different attack, not a multiplier: 1/9 pounce, 2 telekinesis, 3 venom, 4 acid,
    /// 5 radiation, 6 quake, 7 necro + telekinesis, 8 pink + a death blast. The arithmetic lives in
    /// <see cref="ZombieSuperMath"/> so it can be pinned offline; this class owns the state machine.
    /// <c>zombie0</c> — the plain ghoul — authors no <c>ss</c> and therefore behaves exactly as it did
    /// before this workstream.</para>
    ///
    /// <para><b>Resurrection is ported too</b> — the 300-tick <c>t_res</c> countdown
    /// (<c>:495-525</c>) lives on <see cref="ZombieController"/> because it outlives the brain's state
    /// machine; this class drives the countdown and the <c>die</c> row's last 20 ticks with their
    /// <c>die_spark</c> emission (<c>:219-230</c>).</para>
    /// </summary>
    public sealed class ZombieBrain : EnemyBrain
    {
        /// <summary>
        /// AS3 <c>UnitZombie.optDistAtt</c> (<c>UnitZombie.as:79</c>) — the half-width, in pixels, of the
        /// window in which the zombie will even <i>consider</i> a contact attack. Used at
        /// <c>:938</c> as <c>celDX &lt; this.optDistAtt &amp;&amp; celDX &gt; -this.optDistAtt</c>.
        /// </summary>
        private const float OptDistAttackPixels = 200f;
        /// <summary>
        /// The vertical half-extent of the same window — <c>UnitZombie.as:938</c>
        /// <c>celDY &lt; 80 &amp;&amp; celDY &gt; -80</c>. Not a field in the oracle; it is a literal
        /// there and a named constant here so the citation has somewhere to live.
        /// </summary>
        private const float ContactAttackVerticalPixels = 80f;

        /// <summary>
        /// AS3 <c>UnitZombie.digger</c> (<c>UnitZombie.as:120-127</c>): <b>0</b> never buries, <b>1</b>
        /// buried ambusher, <b>2</b> buried with a sharp ear, <b>3</b> buried and inert. Rolled per
        /// placement and carried on <see cref="UnitInstance.digger"/>; the roll itself belongs to
        /// <c>RoomPopulator</c>, because it consumes the spawn RNG.
        /// </summary>
        private int _digger;

        /// <summary>
        /// The unit's unburied <c>ear</c>, remembered so <see cref="Rise"/> can restore it — AS3's
        /// <c>vykop()</c> sets <c>ear = 1</c> (<c>UnitZombie.as:458</c>) rather than restoring a saved
        /// value, but the port's sensor baseline is a serialized field an archetype may have tuned, so
        /// restoring the <i>actual</i> prior value is the faithful translation of "back to normal"
        /// instead of hardcoding 1 over it.
        /// </summary>
        private float _unburiedEar = 1f;

        private bool _buriedEarCaptured;

        // ── The super system — AS3 UnitZombie.as:35-79 and :954-1228 ────────────────────────────────
        //
        // The field names are the oracle's on purpose, so a reader can diff this block against
        // `setSuper()` line by line. Only the units the data actually assigns differ in meaning from
        // AS3's declared defaults.

        /// <summary>
        /// AS3 <c>superSilaTip</c> — the raw <c>&lt;un ss='N'/&gt;</c> value
        /// (<see cref="UnitDefinition.superSkill"/>). <c>0</c> = no super, which is <c>zombie0</c> and
        /// the family template.
        /// </summary>
        private int _superSkill;

        /// <summary>
        /// <see cref="ZombieSuperMath.EffectiveSkill"/> of <see cref="_superSkill"/> — <c>9</c> already
        /// rewritten to <c>1</c>, exactly as <c>setSuper()</c> does (<c>UnitZombie.as:962-969</c>).
        /// Every downstream test reads this, never the raw value.
        /// </summary>
        private int _effectiveSkill;

        /// <summary>AS3 <c>setSuper()</c>'s derived table — timings, speeds and the contact window.</summary>
        private ZombieSuperMath.Tuning _superTuning = ZombieSuperMath.Resolve(0);

        /// <summary>
        /// AS3 <c>aiZlo</c> (<c>:77</c>) — the chase's grudge meter. It grows one per tick while chasing
        /// and fires the super once it passes <c>tZlo</c>.
        /// </summary>
        private int _aiZlo;

        /// <summary>
        /// AS3 <c>tSuper</c>, <b>mutable</b>: the pounce overwrites it with a distance-derived value
        /// (<c>:1116-1120</c>), every other skill keeps the table's constant.
        /// </summary>
        private int _superTicks;

        /// <summary>
        /// AS3 <c>super_on</c> (<c>:65</c>) — true from <c>superSila()</c> until the first tick that is
        /// <i>not</i> state 7 (<c>:741-744</c>). It is what makes the tear-down idempotent and, more
        /// importantly, what makes it happen one tick AFTER the state ends — the oracle's order.
        /// </summary>
        private bool _superOn;

        /// <summary>
        /// AS3 <c>superX</c>/<c>superY</c>, already converted to <b>this port's room-local pixels
        /// (Y up)</b>. <c>superX &lt;= 0</c> is the oracle's "no destination found" sentinel and is kept
        /// as such — <c>findSuper()</c> failing is a real outcome, not an error.
        /// </summary>
        private float _superX;
        private float _superY;

        /// <summary>
        /// The pounce's per-tick velocity in AS3 units (px/frame), re-asserted every tick of state 7.
        /// AS3 holds these constant by setting <c>grav = 0</c>; the port has no per-unit gravity
        /// override, so it corrects the velocity instead — see <see cref="MaintainPounceVelocity"/>.
        /// </summary>
        private float _pounceVelocityX;
        private float _pounceVelocityY;

        /// <summary>
        /// AS3 <c>teleUnit</c> (<c>:49</c>) — the unit being dragged. Nulled by
        /// <see cref="SuperSilaVse"/>, by the target's death, and when the zombie dies.
        /// </summary>
        private UnitController _teleUnit;

        /// <summary>AS3 <c>radMax</c>/<c>radMin</c>/<c>radioactiv</c>/<c>radrad</c> for skill 5.</summary>
        private float _radMax = 1f;
        private float _radMin = 1f;
        private float _radCurrent;
        private float _radRadius = ZombieSuperMath.RadiationRadiusMin;
        private float _radHeal = ZombieSuperMath.DefaultRadHeal;

        /// <summary>AS3 <c>vDestroy</c> — the wall-destroy speed while chasing (state 2/3/7 only).</summary>
        private float _vDestroy = ZombieSuperMath.DefaultVDestroy;

        /// <summary>
        /// The contact window's half-width. AS3 <c>optDistAtt</c> is 200 and ss 7 raises it to 400
        /// (<c>:1019</c>) — a real reach difference, not decoration.
        /// </summary>
        private float _optDistAttack = OptDistAttackPixels;

        /// <summary>
        /// The countdown ticks that still owe a <c>die_spark</c> emission. AS3 emits three sparks per
        /// frame while <c>t_res &lt; 20</c> (<c>:219-230</c>); the port needs to know it already played
        /// them for this death so a corpse re-entering the tick does not spray for ever.
        /// </summary>
        private int _resurrectSparksTicks;

        /// <summary>
        /// The state this unit was in when it fell, restored by <see cref="TickResurrectCorpse"/>.
        /// AS3 does not need a field for this — <c>resurrect()</c> never touches <c>aiState</c>, so the
        /// value simply survives the death. The port's <see cref="EnemyAIState.Dead"/> overwrites it, so
        /// the last live state has to be remembered explicitly.
        /// </summary>
        private EnemyAIState _stateBeforeDeath = EnemyAIState.Idle;

        public override void SetState(EnemyAIState newState)
        {
            if (newState == EnemyAIState.Dead && _currentState != EnemyAIState.Dead)
            {
                _stateBeforeDeath = _currentState;
            }

            base.SetState(newState);
        }

        /// <summary>
        /// A despawned zombie must not leave its victim floating. AS3 clears <c>levit</c> in
        /// <c>superSilaVse()</c> and relies on the unit object surviving; the port destroys the
        /// GameObject on room unload, and the target outlives it.
        /// </summary>
        private void OnDisable()
        {
            ClearTelekinetic();
        }

        protected override void Awake()
        {
            base.Awake();
            ApplyDefinitionMovement();
        }

        public override void Initialize(ITileQueryService tileQuery, SimLoop simLoop)
        {
            base.Initialize(tileQuery, simLoop);

            // Again, because `Awake` runs when the controller is added and the definition only arrives
            // with `Initialize` — RoomUnitSpawner adds the component first and calls
            // `controller.Initialize(definition, stats)` after.
            ApplyDefinitionMovement();
        }

        /// <summary>
        /// AS3 <c>UnitZombie.control()</c>'s <c>throu</c> block (<c>:857-864</c>) and its room-floor guard
        /// (<c>:934-937</c>), evaluated at the end of the brain tick.
        ///
        /// <para><b>Why this wraps <see cref="EnemyBrain.SimTick"/> instead of living inside
        /// <c>TickCombatChase</c>.</b> Two facts about the oracle decide it. The trigger is
        /// <c>aiState == 2 || aiState == 3</c> — the port's <see cref="EnemyAIState.Alert"/> <i>and</i>
        /// <see cref="EnemyAIState.CombatChase"/>, not the chase alone — so a call site in
        /// <c>TickCombatChase</c> would cover half the mechanism and would never fire for a zombie hunting
        /// a sound it cannot see. And the room-floor guard sits <b>outside</b> that branch, so it runs on
        /// every frame whatever the state. One hook at the end of the tick is both: it sees the state this
        /// tick settled into, and it is the last word before the unit's own step reads the flag
        /// (<see cref="EnemyBrain.TickOrder"/> is <c>SimTickOrder.UnitMotor - 1</c>).</para>
        ///
        /// <para><b>It writes <c>false</c>; it does not merely stop writing <c>true</c>.</b> The oracle's
        /// else-branch and its floor guard are both writes. A brain that only asserted the positive case
        /// would leave the flag latched from the last tick the target was below, and the zombie would go on
        /// dropping through every catwalk it met — which is exactly what a "just stop calling it" version
        /// of this hook would do.</para>
        ///
        /// <para><b>The jump rides the same hook, for the same reasons.</b> The oracle's jump trigger is in
        /// the <i>same</i> <c>aiState == 2 || aiState == 3</c> branch (<c>UnitZombie.as:839-842</c>) and is
        /// applied at that branch's end (<c>:923-931</c>), so it wants exactly the same "sees the state this
        /// tick settled into, and is the last word before the unit's own step" placement. The two are
        /// opposite halves of one measurement — <c>celDY</c> — and both live here rather than in the state
        /// ticks.</para>
        /// </summary>
        public override void SimTick(int tickIndex)
        {
            bool wasDead = _controller != null && !_controller.IsAlive;

            if (wasDead)
            {
                // AS3 `UnitZombie.die()` opens with `superSilaVse()` (`:351-355`) — a zombie that dies
                // mid-pull releases its victim rather than leaving them levitated for ever. The port
                // does it on the first dead tick instead of inside the death event, because the brain
                // has no death hook and `_superOn` makes it a one-shot anyway.
                if (_superOn)
                {
                    SuperSilaVse();
                }

                // AS3 `control()` opens with `if(sost == 3) { … return; }` (`UnitZombie.as:536-551`), so
                // the resurrection countdown is the ONLY thing a dead zombie does — no jump cooldown, no
                // state machine, no super. The base still ticks so the corpse animates.
                TickResurrectCorpse();
            }
            else
            {
                // AS3 decrements `aiJump` near the top of `UnitZombie.control()` (`:582-585`), ahead of
                // the `aiState` branch that tests it (`:839`). The order matters only at the boundary
                // where the cooldown reaches zero: the oracle decrements first and tests after, so this
                // does too.
                _jumpCooldownTicks = UnitJumpMath.Tick(_jumpCooldownTicks);
            }

            base.SimTick(tickIndex);

            if (wasDead)
            {
                return;
            }

            // AS3 steps 13-15 of `control()` (`:723-744`) — the grudge meter, the trigger and the
            // tear-down. They run AFTER the state chain and BEFORE the movement branch, which is why
            // this sits between the base tick and the two movement hooks below.
            UpdateSuperState();

            UpdateDropThroughPlatforms();
            UpdateJump(tickIndex);
        }

        /// <summary>
        /// AS3 <c>control()</c>'s <c>sost == 3</c> block (<c>UnitZombie.as:536-551</c>) — the
        /// resurrection countdown, plus the <c>die</c> row's spark shower for its last 20 ticks
        /// (<c>:219-230</c>).
        ///
        /// <para><b>The sparks are emitted here rather than in <c>animate()</c></b> because the port's
        /// animation selection is a pure state lookup and has no business making particles; the oracle
        /// happens to do both in one method, but that is a Flash idiom, not a rule.</para>
        /// </summary>
        private void TickResurrectCorpse()
        {
            if (!(_controller is ZombieController zc) || !zc.CanResurrect)
            {
                return;
            }

            // `if(sost == 3 && isRes && this.t_res < 20)` — three sparks per frame, scattered across the
            // body and up to 10 px above the feet.
            if (zc.ResurrectTimer < 20 && _resurrectSparksTicks != zc.ResurrectTimer && _controller != null)
            {
                _resurrectSparksTicks = zc.ResurrectTimer;

                Vector2 feet = (Vector2)transform.position * 100f;
                float halfWidth = _controller.Stats != null ? _controller.Stats.Width * 50f : 25f;

                for (int i = 0; i < 3; i++)
                {
                    float x = feet.x + (UnityEngine.Random.value - 0.5f) * 2f * halfWidth;
                    float y = feet.y - UnityEngine.Random.value * 10f;
                    _controller.EmitParticle("die_spark", new Vector2(x, y));
                }
            }

            zc.UpdateResurrectTick();

            // `resurrect()` leaves `aiState` alone (`:495-524`) — it only resets `hp`, `sost`, `t_res`,
            // `tZlo` and `aiTCh`. The port therefore restores the state the unit was in when it fell,
            // rather than snapping to Idle: a ghoul that died mid-chase gets straight back up and
            // carries on, which is what makes a `res='1'` variant feel unkillable.
            if (_controller.IsAlive)
            {
                _resurrectSparksTicks = 0;
                SetState(_stateBeforeDeath);
            }
        }

        /// <summary>
        /// AS3 <c>aiJump</c> (<c>UnitZombie.as:75</c>) — the cooldown a jump arms on itself, 30..79 ticks
        /// (<c>:393</c>). <c>0</c> means "may jump"; the oracle tests <c>aiJump &lt;= 0</c>.
        /// </summary>
        private int _jumpCooldownTicks;

        /// <summary>
        /// AS3 <c>UnitZombie.control()</c>'s jump request (<c>:839-842</c>) and its application
        /// (<c>:923-931</c>), evaluated at the end of the brain tick.
        ///
        /// <para><b>The oracle's condition is four terms, and every one of them is here.</b>
        /// <c>aiVNapr &lt; 0 &amp;&amp; this.aiJump &lt;= 0 &amp;&amp; aiTCh % 2 == 1 &amp;&amp;
        /// this.checkJump()</c>, then <c>jump()</c>'s own <c>stay</c> gate and
        /// <c>Unit.as:1890</c>'s <c>jumpdy &lt;= 0</c> refusal.</para>
        ///
        /// <list type="number">
        /// <item><b>the state</b> — <c>aiState == 2 || aiState == 3</c>, i.e. <see cref="EnemyAIState.Alert"/>
        /// <i>and</i> <see cref="EnemyAIState.CombatChase"/>. Not the chase alone: a zombie closing on a
        /// sound it cannot see is state 2, and it is exactly the one that needs to hop up to a noise.</item>
        /// <item><b>the cooldown</b> — <c>aiJump &lt;= 0</c>, decremented by <see cref="SimTick"/>.</item>
        /// <item><b>the parity gate</b> — <c>aiTCh % 2 == 1</c>. See the note below on why the port reads the
        /// tick index instead.</item>
        /// <item><b>the target overhead</b> — <see cref="UnitJumpMath.ShouldJump"/>, from the same two
        /// quantities <see cref="UpdateDropThroughPlatforms"/> uses, with the opposite sign.</item>
        /// <item><b>grounded</b> — AS3's <c>stay</c>, checked <i>inside</i> <c>jump()</c>. Without it a
        /// zombie mid-arc could re-jump and appear to hover: the cooldown's floor (30 ticks) is shorter than
        /// the flight time (~36 ticks for <c>jumpdy</c> 18 under 1 px/frame² gravity), so the cooldown alone
        /// does not cover the arc.</item>
        /// <item><b>headroom</b> — <c>checkJump()</c>, four probes at 85/125 px above the feet in this
        /// column and the one ahead.</item>
        /// </list>
        ///
        /// <para><b>The parity gate is the sim tick's, not a per-zombie counter — a named divergence.</b>
        /// AS3's <c>aiTCh</c> is re-armed to a random 100..199 whenever it hits zero (<c>:641-648</c>), so
        /// its parity phase is effectively random per re-arm; the port has no per-zombie equivalent and
        /// reads <c>tickIndex</c>. The <i>effect</i> — half the attempts — is identical, and the per-zombie
        /// cooldown roll already desynchronises units, so the observable cadence matches. Verified that
        /// <c>aiTCh</c> is re-armed at all, because a counter that only ever clamps at 0 would make
        /// <c>% 2 == 1</c> a gate that can never open — and the fix would look inert.</para>
        /// </summary>
        private void UpdateJump(int tickIndex)
        {
            if (_currentState != EnemyAIState.Alert && _currentState != EnemyAIState.CombatChase)
            {
                return;
            }

            if (_jumpCooldownTicks > 0)
            {
                return;
            }

            // AS3 `aiTCh % 2 == 1` — every other tick.
            if ((tickIndex & 1) != 1)
            {
                return;
            }

            Vector2 eyePx = GetEyePositionPixels();
            float unitTopPx = eyePx.y + UnitHeightPixels * 0.5f;
            float targetCentrePx = eyePx.y + _blackboard.TargetDeltaY;

            if (!UnitJumpMath.ShouldJump(unitTopPx, targetCentrePx))
            {
                return;
            }

            // AS3 `jump()`'s `if(stay)`, and `Unit.as:1890`'s `jumpdy <= 0` refusal. `bigrobot` carries
            // `jumpForce: 0`, so "cannot jump" is a real data-driven property and not a missing value.
            float jumpForce = (_controller != null && _controller.Stats != null)
                ? _controller.Stats.JumpForce
                : 0f;

            if (!_controller.IsGrounded || jumpForce <= 0f)
            {
                return;
            }

            if (!HasJumpHeadroom())
            {
                return;
            }

            JumpVertical(jumpForce);

            // AS3 `aiJump = Math.floor(30 + Math.random() * 50)` (:393). An int draw, not a float one:
            // `Random.Range(0f, 50f)` is inclusive of its upper bound and can produce 80.
            _jumpCooldownTicks = UnitJumpMath.CooldownTicks(
                UnityEngine.Random.Range(0, UnitJumpMath.CooldownRollRange));
        }

        /// <summary>
        /// AS3 <c>UnitZombie.checkJump()</c> (<c>UnitZombie.as:465-484</c>) — the headroom probe.
        ///
        /// <para><b>Four probes, and any one of them being non-air refuses the jump.</b> Two heights above
        /// the feet (85 and 125 px) in the unit's own column and in the column one tile toward its facing.
        /// The oracle writes it as four early <c>return false</c>s; the port writes it as four
        /// <c>&amp;&amp;</c>s, which is the same predicate.</para>
        ///
        /// <para><b><c>phis != 0</c> maps to <c>IsWallAtRoomLocalPixels</c>, and that is not an
        /// approximation.</b> AS3 asks whether the tile is <i>anything but air</i>, which reads like it
        /// should map to "not <c>Air</c>" here — and it should not. The port's
        /// <see cref="TilePhysicsType"/> splits AS3's <c>phis == 0</c> case into the geometry markers
        /// <c>Platform</c> and <c>Stair</c> (<c>TilePhysicsType.cs:7-21</c>), and <c>TileDecoder</c>'s
        /// <c>MapPhysicsType</c> maps <b>every non-zero <c>phis</c> to <c>Wall</c></b>
        /// (<c>TileDecoder.cs:296-303</c>). So <c>physicsType == Wall</c> <i>is</i> the port's encoding of
        /// "AS3 <c>phis</c> is non-zero" (<c>TileData.cs:205-209</c>), and a catwalk or ladder — air to AS3
        /// — is correctly <b>not</b> a headroom blocker.</para>
        ///
        /// <para><b>No room to ask is not a refusal.</b> A brain spawned without a room cannot answer the
        /// question, and refusing would make a bare test spawn permanently unable to jump for a reason that
        /// has nothing to do with headroom. <c>true</c> is the honest degradation: the caller has already
        /// established the unit is grounded and has an impulse.</para>
        /// </summary>
        private bool HasJumpHeadroom()
        {
            if (_tileQuery == null || _tileQuery.Room == null)
            {
                return true;
            }

            // Room-local pixels: world pixels minus the room origin, the same space `HasWallUnderFeet`
            // uses and the space AS3's `X`/`Y` are already in.
            Vector2 origin = _tileQuery.OriginPixel;
            float feetX = transform.position.x * 100f - origin.x;
            float feetY = transform.position.y * 100f - origin.y;

            int facing = _controller != null ? _controller.FacingDirection : 1;

            float aheadX = UnitJumpMath.HeadroomAheadPixelX(feetX, facing);
            float lowY = UnitJumpMath.HeadroomLowPixelY(feetY);
            float highY = UnitJumpMath.HeadroomHighPixelY(feetY);

            RoomInstance room = _tileQuery.Room;

            return !room.IsWallAtRoomLocalPixels(new Vector2(feetX, lowY))
                && !room.IsWallAtRoomLocalPixels(new Vector2(feetX, highY))
                && !room.IsWallAtRoomLocalPixels(new Vector2(aheadX, lowY))
                && !room.IsWallAtRoomLocalPixels(new Vector2(aheadX, highY));
        }

        // ── The ledge sense: AS3 `shX1`/`shX2` ──────────────────────────────────────────────────────
        //
        // `UnitZombie.control()` reads the unit's overhang in three states, and two of them can hop:
        //
        //   aiState == 0    :753-763   at a lip -> request a turn. NO hop.
        //   aiState == 1    :789-835   at a lip -> 10% of ticks: probe 80 px ahead at floor level and hop
        //                              a crate if one is there; otherwise (and the other 90%) turn around.
        //   aiState == 2/3  :890-900   at a lip -> if the target is not below, 50% chance of a HALF hop.
        //
        // The idle site is SUBSUMED rather than ported separately, and that is a finding rather than a
        // shortcut: its `turnX` is not consumed anywhere in its own branch — it survives until state 1,
        // where `:827-835` applies it. So the idle site exists to make a zombie that resumes patrolling
        // at a lip walk AWAY from it, and this port gets that for free because the patrol's hook runs
        // before the patrol's move: the very first patrol tick at a lip turns the zombie around before it
        // takes a step. Same outcome, one tick later, and no second copy of the turn. See doc 26 §3.
        //
        // All four jump sites share one cooldown (`aiJump`), armed by `jump()` on every call — so a ledge
        // hop suppresses the next target-overhead jump, but the ledge sites are themselves NOT gated on
        // the cooldown (there is no `aiJump <= 0` test at :789 or :890), and neither is this.

        /// <summary>
        /// AS3 <c>UnitZombie.as:791</c>/<c>:810</c> — <c>isrnd(0.1)</c>: the chance, per tick at a lip,
        /// that the zombie even <i>looks</i> for a crate before turning around.
        /// </summary>
        private const float PatrolProbeChance = 0.1f;

        /// <summary>
        /// AS3 <c>UnitZombie.as:829</c> — <c>if(isrnd(0.1)) { aiState = 0; }</c>: one turn in ten also
        /// drops the zombie to idle before it resumes patrolling.
        /// </summary>
        private const float PatrolIdleChance = 0.1f;

        /// <summary>
        /// AS3 <c>UnitZombie.as:892</c> — <c>isrnd(0.5)</c>, the chase's chance of taking the half hop
        /// instead of braking.
        /// </summary>
        private const float ChaseHopChance = 0.5f;

        /// <summary>
        /// The zombie's reaction to the edge of what it is standing on — AS3's three <c>shX</c> sites.
        /// Called by the base <i>before</i> the move in <c>TickPatrol</c>, <c>TickAlert</c> and
        /// <c>TickCombatChase</c>; see <see cref="EnemyBrain.HandleLedgeAhead"/> for why that placement
        /// is load-bearing.
        ///
        /// <para><b>The guard is not defensive padding.</b> Without a controller there is no body width,
        /// and without a room there is no support span — and with no support span
        /// <c>UnitController.ResolveGroundState</c> leaves the overhang at its "nothing supports me"
        /// value of <c>1</c>, which every threshold here reads as "at a lip". Reacting to that would turn
        /// a room-less zombie around on every single tick. The oracle cannot be in this state: its
        /// <c>control()</c> only runs with a <c>loc</c>, and <c>shX1</c> is <c>NaN</c> until the first
        /// ground pass, which compares false against every threshold.</para>
        /// </summary>
        protected override void HandleLedgeAhead(int tickIndex)
        {
            if (_controller == null || _tileQuery == null || _tileQuery.Room == null)
            {
                return;
            }

            int facing = _controller.FacingDirection;

            if (_currentState == EnemyAIState.Patrol)
            {
                HandlePatrolLedge(facing);
                return;
            }

            HandleChaseLedge(facing);
        }

        /// <summary>
        /// AS3 <c>UnitZombie.as:789-835</c> — the patrol's hop-or-turn at a lip.
        /// </summary>
        private void HandlePatrolLedge(int facing)
        {
            // `stay && shX1 > 0.25 && aiNapr < 0`, mirrored for shX2 (:789 / :808). `stay` is this port's
            // IsGrounded; `aiNapr < 0` is the facing, which is what OverhangAhead selects on.
            if (!_controller.IsGrounded
                || !UnitOverhangMath.IsAtEdgeToward(
                    facing,
                    _controller.OverhangLeft,
                    _controller.OverhangRight,
                    UnitOverhangMath.PatrolEdgeThreshold))
            {
                return;
            }

            // :791-796 — the crate test, and it is the MINORITY branch. `isrnd(0.1)` gates both arms, so
            // nine ticks in ten the zombie turns around without probing at all. A zombie therefore hops
            // onto a crate only after a few visits to the same lip: it turns, walks away, comes back,
            // rolls again. That is the oracle's behaviour and not a bug to "fix" — the play-test
            // expectation is in doc 26 §6.
            //
            // `&&` and not a nested `if`: the short circuit is the oracle's, and it means the probe is
            // only performed on the 10% of ticks the roll passes.
            if (UnityEngine.Random.value < PatrolProbeChance && HasHopSurfaceAhead(facing))
            {
                HalfHop();
                return;
            }

            TurnAroundAtLip(facing);
        }

        /// <summary>
        /// AS3 <c>UnitZombie.as:890-900</c> — the chase's half hop at a lip.
        /// </summary>
        private void HandleChaseLedge(int facing)
        {
            // :890 — `stay && (shX1 > 0.5 && aiNapr < 0 || shX2 > 0.5 && aiNapr > 0)`. The chase's
            // threshold is double the patrol's: by the time half the body is past the lip the zombie is
            // already committed, where the patrol acts at a quarter.
            if (!_controller.IsGrounded
                || !UnitOverhangMath.IsAtEdgeToward(
                    facing,
                    _controller.OverhangLeft,
                    _controller.OverhangRight,
                    UnitOverhangMath.ChaseEdgeThreshold))
            {
                return;
            }

            // :892 — `aiVNapr <= 0`, "the target is above or level with my top". That is exactly the
            // negation of UnitJumpMath.ShouldJump's `> 40` band, so the two share one definition instead
            // of restating the threshold — and the point of the guard is that jumping at a target which
            // is BELOW you is pointless, so the oracle brakes instead. Site 1 (`:839`) owns the
            // strictly-above case and jumps at full force.
            Vector2 eyePx = GetEyePositionPixels();
            float unitTopPx = eyePx.y + UnitHeightPixels * 0.5f;
            float targetCentrePx = eyePx.y + _blackboard.TargetDeltaY;

            if (UnitJumpMath.ShouldJump(unitTopPx, targetCentrePx))
            {
                return;
            }

            // :896-899 — the `else` arm is `dx *= 0.6`, the brake. Deliberately not ported: AS3's `dx`
            // accumulates and is clamped, so the brake only delays the fall, and this port's
            // MoveHorizontal assigns an absolute velocity every tick so a 60% reduction could not survive
            // to the next one. Recorded in doc 26 §3 rather than approximated.
            if (UnityEngine.Random.value < ChaseHopChance)
            {
                HalfHop();
            }
        }

        /// <summary>
        /// AS3 <c>_loc2_ = 0.5</c> — the <b>half-strength</b> hop, shared by the crate site
        /// (<c>UnitZombie.as:796</c>) and the chase site (<c>:894</c>). <c>jump(param1)</c> applies
        /// <c>dy = -jumpdy * param1</c> (<c>:388-399</c>), so this reaches half the height of site 1's
        /// full hop. A crate is one tile; a full hop would overshoot it and put the zombie's head into the
        /// ceiling.
        ///
        /// <para><b>It arms the cooldown and is not gated by it.</b> Both halves are the oracle's:
        /// <c>jump()</c> always sets <c>aiJump = floor(30 + random()*50)</c>, but neither ledge site tests
        /// <c>aiJump &lt;= 0</c> — only site 1 does (<c>:839</c>). So a ledge hop suppresses the next
        /// overhead jump while remaining available itself.</para>
        ///
        /// <para><b>No headroom probe, and that is faithful rather than an omission.</b> Sites 1 and 3
        /// call <c>checkJump()</c>; sites 2 and 4 do not. A half hop's apex is about 40 px — under one
        /// tile — which is why the oracle can skip it here.</para>
        /// </summary>
        private void HalfHop()
        {
            float jumpForce = (_controller != null && _controller.Stats != null)
                ? _controller.Stats.JumpForce
                : 0f;

            // AS3 `jump()`'s `if(stay)` — already established by the caller's IsGrounded gate — and
            // `Unit.as:1890`'s `jumpdy <= 0` refusal. `bigrobot` carries `jumpForce: 0`, so "cannot jump"
            // is a real data-driven property and not a missing value.
            if (jumpForce <= 0f)
            {
                return;
            }

            JumpVertical(jumpForce * UnitOverhangMath.CrateHopMagnitude);

            _jumpCooldownTicks = UnitJumpMath.CooldownTicks(
                UnityEngine.Random.Range(0, UnitJumpMath.CooldownRollRange));
        }

        /// <summary>
        /// AS3 <c>UnitZombie.as:827-835</c> — the patrol's turn: <c>aiNapr = storona = turnX</c>, with a
        /// 10% chance of dropping to <c>aiState = 0</c> on the way.
        ///
        /// <para><b>The oracle turns by flipping its desired direction, and commits in the same tick</b> —
        /// there is no timer in this branch. That is why the patrol does not need AS3's <c>aiTTurn</c>
        /// model: <c>turnX</c> is written and consumed inside one branch. The timer belongs to site 3,
        /// which stays unported (doc 26 §3).</para>
        ///
        /// <para>The flip is what the base's subsequent <c>MoveHorizontal</c> follows, so the zombie walks
        /// away from the lip on this very tick rather than stepping off it.</para>
        /// </summary>
        private void TurnAroundAtLip(int facing)
        {
            int turned = -facing;
            _blackboard.FacingDirection = turned;
            ApplyFacing(turned);

            if (UnityEngine.Random.value < PatrolIdleChance)
            {
                SetState(EnemyAIState.Idle);
            }
        }

        /// <summary>
        /// AS3 <c>UnitZombie.as:793</c>/<c>:812</c> — <c>loc.getAbsTile(X + storona * 80, Y + 10)</c>,
        /// asked as <c>_loc1_.phis == 1 || _loc1_.shelf</c>: is there a solid column or a shelf tile 80 px
        /// ahead <b>at floor level</b> — something the zombie can hop onto?
        ///
        /// <para><b>The point is room-local pixels, and its vertical offset is subtracted.</b> AS3's
        /// <c>Y + 10</c> is 10 px <i>below</i> the feet, because AS3's Y grows downward; this port's
        /// room-local Y grows upward, so the probe is <c>feetY - 10</c>. That conversion is
        /// <see cref="UnitOverhangMath.AheadProbePoint"/>'s job — see its remarks, because getting the sign
        /// wrong puts the probe above the zombie's head where there is never a crate, and the zombie then
        /// turns around at every lip and never hops, with no exception and nothing in a log.</para>
        ///
        /// <para>Out of bounds answers <c>false</c>, which is AS3's answer and not a fallback:
        /// <c>getAbsTile</c> returns the sentinel <c>otstoy</c>, whose <c>phis</c> and <c>shelf</c> are
        /// both zero-initialised (<c>Tile.as:18</c>, <c>:20</c>).</para>
        /// </summary>
        private bool HasHopSurfaceAhead(int facing)
        {
            Vector2 origin = _tileQuery.OriginPixel;
            float feetX = transform.position.x * 100f - origin.x;
            float feetY = transform.position.y * 100f - origin.y;

            Vector2 probe = UnitOverhangMath.AheadProbePoint(feetX, feetY, facing);
            return _tileQuery.Room.IsSolidOrShelfAtRoomLocalPixels(probe);
        }

        /// <summary>
        /// This tick's <c>throu</c> level, from <c>celDY</c>'s two terms plus the room-floor guard.
        /// <para><b>The trigger.</b> <c>celDY = celY - Y + scY</c> (<c>UnitZombie.as:678</c>) reduces to
        /// <i>the target's centre minus this unit's top</i>, because <c>Y1 = Y - scY</c> is the unit's top
        /// and <c>celY</c> is the target's centre (<c>Unit.as:1875-1876</c>). Both quantities already
        /// exist here: the unit's top is its centre plus half its height, and the target's centre is this
        /// unit's eye plus <see cref="EnemyBlackboard.TargetDeltaY"/> — which <c>EnemySensors</c> defines
        /// as <c>targetCentre - eyePos</c> (<c>EnemySensors.cs:331</c>), the eye being the unit's centre
        /// (<see cref="EnemyBrain.GetEyePositionPixels"/>). Composing them, rather than re-deriving the
        /// target's position from its transform, keeps one definition of "where the target is" — and the
        /// eye/target-centre pair is the one AS3 itself compares.</para>
        ///
        /// <para><b><c>TargetDeltaY</c> is the right quantity even in <c>Alert</c>, where the target is
        /// not visible.</b> It is written only on a sighting commit, so outside combat it holds the last
        /// one — which is precisely the oracle's <c>celY</c>: the alert branch re-jitters <c>celX</c> but
        /// passes <c>celY</c> straight through (<c>:666</c>). A target that has never been seen leaves it
        /// at zero, i.e. level with this unit's centre; the band is 80 px, so that reads as "do not drop"
        /// rather than as "drop to the bottom of the world", which is the safe direction to fail.</para>
        ///
        /// <para><b>The guard.</b> <c>if(Y &gt; loc.spaceY * Tile.tileY - 80) { throu = false; }</c>
        /// (<c>:934-937</c>) stops a zombie walking itself out of the room while chasing a target it can
        /// never reach downwards. <c>loc.spaceY * Tile.tileY</c> is the room's bottom edge in room-local
        /// pixels, and <c>OriginPixel.y</c> is the world-pixel position of that same edge — tile row 0 is
        /// the <i>lowest</i> row, which is what makes
        /// <c>TileCollisionMath</c>'s <c>tileTop = roomWorldPixelY + (tileY + 1) * TILE_SIZE</c> come out
        /// right — so both sides are the same comparison and the origin cancels.</para>
        /// </summary>
        private void UpdateDropThroughPlatforms()
        {
            bool wantsDrop = false;

            if (_currentState == EnemyAIState.Alert || _currentState == EnemyAIState.CombatChase)
            {
                Vector2 eyePx = GetEyePositionPixels();
                float unitTopPx = eyePx.y + UnitHeightPixels * 0.5f;
                float targetCentrePx = eyePx.y + _blackboard.TargetDeltaY;

                float unitOriginPx = transform.position.y * 100f;
                float roomFloorPx = _tileQuery != null ? _tileQuery.OriginPixel.y : 0f;

                wantsDrop = UnitDropThroughMath.ShouldDropThrough(unitTopPx, targetCentrePx)
                    && !UnitDropThroughMath.IsNearRoomFloor(unitOriginPx, roomFloorPx);
            }

            SetDropThroughPlatforms(wantsDrop);
        }

        /// <summary>
        /// This unit's collision height in pixels — AS3 <c>scY</c>. Deliberately the same expression and
        /// the same fallback as <see cref="EnemyBrain.GetEyePositionPixels"/>, so "how tall is this unit"
        /// has one answer: if the two disagreed, the eye would sit at one height and the top at another,
        /// and the 80 px band would be measured from a unit that does not exist.
        /// </summary>
        private float UnitHeightPixels => (_controller != null && _controller.Stats != null)
            ? _controller.Stats.Height * 100f
            : 50f;

        /// <summary>
        /// Read the zombie's speed ladder off its <see cref="UnitDefinition"/> instead of the serialized
        /// placeholders.
        ///
        /// <para><b>Why this is not just tidiness.</b> The placeholders were <c>1.5</c>/<c>3.5</c>, which
        /// happen to be close to <c>zombie0</c>'s walk speed and nothing like its run speed. AS3's run
        /// speed is an <b>absolute</b> figure from <c>&lt;move run=…&gt;</c>
        /// (<c>Unit.as:1070-1073</c>) and <c>zombie0</c> is <c>speed='1.5' run='10'</c> — so a chasing
        /// zombie moves 6.7× faster than a patrolling one, and it is the run speed, not a tuned constant,
        /// that decides whether it can ever catch the player (whose walk is 7 px/frame).</para>
        ///
        /// <para>A null definition leaves the serialized values in place, which is the right degradation
        /// for a bare test spawn — there is no data to be faithful to.</para>
        /// </summary>
        private void ApplyDefinitionMovement()
        {
            UnitDefinition definition = _controller != null ? _controller.Stats : null;
            if (definition == null)
            {
                return;
            }

            // AS3 UnitZombie.as:367 `walkSpeed = maxSpeed;`, with `maxSpeed` from
            // `node.@speed` (Unit.as:1068). aiState 0/1 move at `maxSpeed = walkSpeed` (:708).
            _patrolSpeed = definition.WalkSpeed;

            // AS3 UnitZombie.as:709 `maxSpeed = runSpeed * 0.6;` at aiState 2, and :714
            // `maxSpeed = runSpeed;` at aiState 3. `runSpeed` is `node.@run` (Unit.as:1072).
            _alertSpeed = definition.RunSpeed * 0.6f;
            _chaseSpeed = definition.RunSpeed;

            ApplyDefinitionSuper(definition);
        }

        /// <summary>
        /// AS3 <c>setSuper()</c> (<c>UnitZombie.as:954-1037</c>) — turn the row's <c>ss</c> into the
        /// timing table, the weapon and the aura constants.
        ///
        /// <para><b>Called from the same place the oracle calls it</b> — the constructor, after
        /// <c>getXmlParam()</c> has read <c>ss</c>. Here that is <c>OnDefinitionAssigned</c>, because the
        /// port's definition arrives with <c>Initialize</c> rather than with the constructor.</para>
        /// </summary>
        private void ApplyDefinitionSuper(UnitDefinition definition)
        {
            _superSkill = definition.superSkill;
            _effectiveSkill = ZombieSuperMath.EffectiveSkill(_superSkill);
            _superTuning = ZombieSuperMath.Resolve(_superSkill);
            _superTicks = _superTuning.TSuper;
            _optDistAttack = _superTuning.OptDistAttack;
            _vDestroy = _superTuning.VDestroy;

            if (_effectiveSkill == ZombieSuperMath.Radiation)
            {
                // `radioactiv = radMin = 1; radMax = 20; radrad = radradMin = 120; radradMax = 1200;`
                // (:1004-1007). `setLevel` scales radMax by 1 + 0.05*level and radHeal by 1 + 0.16*level
                // (:171-172); the port has no unit level at spawn, so both keep their base values and
                // that is recorded in the audit rather than guessed at.
                _radMin = 1f;
                _radMax = 20f;
                _radCurrent = _radMin;
                _radRadius = ZombieSuperMath.RadiationRadiusMin;
                _radHeal = ZombieSuperMath.DefaultRadHeal;
            }
        }

        // ── The digger: a zombie that waits buried in the floor ─────────────────────────────────────
        //
        // The whole feature is four oracle methods, and the port keeps their names so a reader can
        // diff them: `setPos` decides whether to bury at all (:192-213), `zakop` buries (:414-445),
        // `vykop` rises (:447-463), and `control`/`alarma` decide when (:574-606, :328-349).
        //
        // One thing it deliberately does NOT model: `invis = true` (:424). AS3 sets it so the player's
        // targeting skips a buried unit. The port has no enemy invisibility flag and nothing that reads
        // one, so inventing a property that only this class would honour would look like a port and
        // protect nothing. The player can therefore shoot a buried zombie's collider; it will dig out
        // (OnDamaged -> BeginDigging), which is the visible half of the same behaviour.

        /// <summary>
        /// AS3 <c>UnitZombie.setPos</c> (<c>UnitZombie.as:192-213</c>) — the bury.
        ///
        /// <para>Both facts come from the placement, resolved by <c>RoomPopulator</c> where the oracle
        /// resolves them: the tier, because the roll consumes the spawn RNG, and the ground test, because
        /// it is a question about the room as it was built. This method only acts on them.</para>
        ///
        /// <para><b>Nothing here re-checks the ground.</b> A tier-1 unit over a catwalk has
        /// <c>zak = false</c> in the oracle and stands as an ordinary ghoul — it does not bury, and it
        /// does not get a second chance later. Re-testing here would bury it the moment it walked onto
        /// solid floor, which is a different game.</para>
        /// </summary>
        public override void OnPlacementApplied(UnitInstance placement)
        {
            base.OnPlacementApplied(placement);

            if (placement == null)
            {
                return;
            }

            _digger = placement.digger;

            if (placement.ambushArmed)
            {
                Bury();
            }
        }

        /// <summary>
        /// AS3 <c>zakop()</c> (<c>UnitZombie.as:414-445</c>).
        ///
        /// <para>The oracle also zeroes <c>knocked</c>, sets <c>scY = 0</c> with <c>Y1 = Y2</c> (the unit
        /// sinks into its own floor line), clears <c>levitPoss</c>/<c>activateTrap</c>/<c>stealthMult</c>
        /// and sets <c>overLook</c>. None of those has a port field; the ones that do are handled by
        /// <see cref="EnemyAIState.Buried"/> in <see cref="EnemyBrain.SetState"/> (the movement half) and
        /// below (the senses).</para>
        /// </summary>
        private void Bury()
        {
            SetState(EnemyAIState.Buried);

            // AS3 zakop() picks vision and ear from the tier (:426-439). Only `ear` survives, and the
            // reason is worth writing down rather than leaving as an unexplained asymmetry: control()
            // recomputes `vision` on every tick it does not early-return on (:696-705,
            // `vision = aiSpok == 0 ? 0.7 : 1`), so zakop's `vision` is overwritten within one frame for
            // tiers 1 and 2 and is never read at all for tier 3. `ear` is not touched by control(), so
            // it is the half that persists — and it is what makes a tier-2 ambusher (vision 0, ear 1.8)
            // notice the player by sound before it can be seen.
            //
            // ORDERING THIS DEPENDS ON: `EnemyBrain.SyncSensorTuning()` copies the unit's `ear` from the
            // definition into this same field, and it would silently undo the override below. It is safe
            // only because `RoomUnitSpawner` calls `Initialize` (→ `OnDefinitionAssigned` → sync) BEFORE
            // `ApplyPlacement` (→ `OnPlacementApplied` → this method), and nothing re-syncs per tick. If
            // a future change makes the sync periodic, the buried ear must move into the sync's own
            // source (a `UnitController` override) rather than being written here.
            if (!_buriedEarCaptured)
            {
                _unburiedEar = _sensors.Ear;
                _buriedEarCaptured = true;
            }

            _sensors.Ear = _digger switch
            {
                1 => 0.2f,
                2 => 1.8f,
                _ => 0f
            };

            // Draw — or rather un-draw — it on the frame it buries. SetState only calls UpdateAnimation
            // for Dead, so without this the unit stays on screen for the tick between ApplyPlacement and
            // the first SimTick, which is precisely the frame the player would notice it standing in the
            // open. Same reasoning as the corpse case, one state over.
            UpdateAnimation();
        }

        /// <summary>
        /// AS3's two promotion sites, which are the same two lines: <c>alarma()</c>
        /// (<c>UnitZombie.as:337-341</c>) and <c>control()</c>'s <c>celUnit</c> arm (<c>:597-601</c>)
        /// both do <c>aiState = 6; aiTCh = 24;</c>.
        /// </summary>
        private void BeginDigging()
        {
            SetState(EnemyAIState.Digging);
        }

        /// <summary>
        /// AS3 <c>vykop()</c> (<c>UnitZombie.as:447-463</c>).
        ///
        /// <para><b>It never goes back.</b> <c>aiState = 3</c> is the last state it writes, and nothing in
        /// <c>UnitZombie</c> ever assigns 5 again — so a zombie that has climbed out stays out, and the
        /// only way to meet a buried one is to arrive before it wakes. That is the oracle's behaviour and
        /// not an omission: the concealment is a one-shot opening gambit, and <c>setPos</c>'s
        /// <c>!loc.active</c> guard is what stops a later reposition from re-arming it.</para>
        ///
        /// <para>The port gets that guarantee structurally rather than from a flag:
        /// <see cref="Bury"/> is reachable only from <see cref="OnPlacementApplied"/>, which the spawner
        /// calls once per spawned unit. There is deliberately no <c>_armed</c> field to clear, because a
        /// field nothing reads is the shape this codebase keeps getting bitten by — it looks like the
        /// mechanism and is not.</para>
        /// </summary>
        private void Rise()
        {
            if (_buriedEarCaptured)
            {
                _sensors.Ear = _unburiedEar;
                _buriedEarCaptured = false;
            }

            // `vykop()` also does `aiSpok = maxSpok + 10` (`UnitZombie.as:453`) — it wakes up already
            // committed to the hunt rather than drifting back into patrol.
            //
            // This used to be `_patrolDurationTicks` (90), recorded as "the nearest honest stand-in"
            // because the port had no `maxSpok`. It has one now — `EnemyAwarenessMath` — and the
            // approximation is no longer merely imprecise, it is BROKEN: `TickCombatChase` now exits
            // below `EnemyAwarenessMath.ChaseThresholdTicks` (300), so an ambusher armed with 90 would
            // pop out of the ground, enter the chase, and drop straight back to `Alert` on its first
            // tick — the opposite of "wakes up already committed". The oracle's own value fixes it.
            _blackboard.AlertTimerTicks = EnemyAwarenessMath.FullAwarenessTicks;

            SetState(EnemyAIState.CombatChase);
        }

        /// <summary>
        /// Whether the two tiles under this unit's feet are still walls — the live half of
        /// <c>setPos</c>'s test, re-run every buried tick because AS3 re-reads <c>kop1.phis</c> and
        /// <c>kop2.phis</c> rather than caching them (<c>UnitZombie.as:574</c>). That is what makes a
        /// buried zombie pop out when the floor above it is destroyed.
        /// </summary>
        private bool HasWallUnderFeet()
        {
            if (_tileQuery == null)
            {
                // No room to ask. AS3 cannot be in this state either — `setPos` guards on `loc` — so the
                // honest answer is "not buried", which makes a bare test spawn behave like a plain ghoul
                // instead of hiding forever.
                return false;
            }

            Vector2 roomLocalFeet =
                (Vector2)transform.position * (1f / TileQueryConstants.PixelToUnit) - _tileQuery.OriginPixel;

            return _tileQuery.Room != null && _tileQuery.Room.HasWallUnderFeet(roomLocalFeet);
        }

        /// <summary>
        /// AS3 <c>UnitZombie.control()</c>'s <c>aiState == 5</c> arms (<c>UnitZombie.as:574-606</c>), in
        /// the oracle's order — which is not the order a reader would guess, and two of the three
        /// orderings are load-bearing.
        /// </summary>
        protected override void TickBuried(int tickIndex)
        {
            // :574-577 — the floor gave way. FIRST, and ahead of the tier-3 early return below, so even
            // an inert ambusher pops up when the ground under it is destroyed. It does not then chase:
            // control() returns immediately on every later tick (:578-581), so a tier-3 unit that rises
            // simply stands where it rose.
            if (!HasWallUnderFeet())
            {
                Rise();
                return;
            }

            // :578-581 — `if(this.digger == 3) return;`. A holding pen: no wake rule is acted on,
            // alarma() returns (:330-333), and the unit waits indefinitely. It is still damageable, and
            // being damaged does not wake it either — see OnDamaged.
            if (_digger == 3)
            {
                return;
            }

            // :595-601 — `if(celUnit) { aiState = 6; aiTCh = 24; } else { aiTCh = 30; }`.
            if (IsTargetInAmbushReach())
            {
                BeginDigging();
                return;
            }

            // The `else` arm. It is not a no-op: aiTCh is the shared sub-state timer, and leaving a
            // stale value in it would let a future `aiState == 6` check fire on a counter that was never
            // armed for digging.
            _blackboard.StateTimerTicks = 30;
        }

        /// <summary>
        /// The buried state's wake condition — AS3's <c>celUnit</c>, the unit standing in the probe cell
        /// ahead (<c>UnitZombie.as:597</c>).
        ///
        /// <para><b>This is an approximation, and it is a deliberate one.</b> AS3 sets <c>celUnit</c> in
        /// <c>findCel()</c>, which samples a single point at <c>X + scX * storona * 2</c> — a cell-wide
        /// probe immediately in front. The port has no cell probe; it has
        /// <see cref="EnemySensors"/>, which fills <see cref="EnemyBlackboard.TargetUnit"/> from a vision
        /// cone plus a line-of-sight raycast and flags <c>HasHeardNoise</c> for anything it hears. Using
        /// the sensors is the better model of "the player has arrived", and it is the only perception the
        /// port has — but it is <i>wider</i> than the oracle's, so an ambusher here can wake to a player
        /// standing still in the open several tiles away where AS3's would sleep until they stepped into
        /// the cell.</para>
        /// </summary>
        private bool IsTargetInAmbushReach()
        {
            return _blackboard.TargetUnit != null || _blackboard.HasHeardNoise;
        }

        /// <summary>
        /// AS3 <c>UnitZombie.control()</c>'s <c>aiState == 6</c> arm (<c>UnitZombie.as:574-594</c>).
        /// </summary>
        protected override void TickDigging(int tickIndex)
        {
            // The floor check comes first here too (:574-577), so a unit that is halfway out when the
            // ground is destroyed finishes immediately rather than after the countdown.
            if (!HasWallUnderFeet())
            {
                Rise();
                return;
            }

            // :586-594 — `if(aiTCh > 0) --aiTCh; else if(aiState == 6) { vykop(); aiTCh = 30; }`. The
            // shared helper owns the two-tick shape of that; see its remarks for why collapsing it would
            // make the dig one tick short.
            if (AdvanceDigTimer())
            {
                Rise();
            }
        }

        /// <summary>
        /// AS3 <c>UnitZombie.alarma()</c> (<c>UnitZombie.as:328-349</c>) — the override that decides
        /// whether noise or a hit is allowed to wake this unit.
        ///
        /// <para><b>Three things the base implementation gets wrong for a zombie</b>, which is why this
        /// override exists rather than being a nicety:</para>
        ///
        /// <list type="number">
        /// <item>a <b>tier-3</b> unit returns immediately (<c>:330-333</c>) — noise and damage do nothing
        /// at all, so it can be shot while buried and will not so much as turn over;</item>
        /// <item>a <b>buried</b> unit does not enter the alert ladder, it <b>digs out</b> (<c>:337-341</c>) —
        /// the base would leave it lying in the floor while "alerted", which is both wrong and
        /// invisible;</item>
        /// <item>a unit that is <b>already digging</b> ignores the alarm entirely, because the guard is
        /// <c>aiState &lt;= 1 || aiState == 5</c> (<c>:334</c>) — so a second hit cannot restart or
        /// extend the climb out.</item>
        /// </list>
        /// </summary>
        public override void OnDamaged(float amount, Vector2 damageSourcePx)
        {
            if (_digger == 3)
            {
                return;
            }

            if (_currentState == EnemyAIState.Buried)
            {
                // `super.alarma()` runs before the state change in the oracle (:336), so the unit still
                // records where the noise came from — which is what it will chase once it is out.
                //
                // `caughtUnawares: true` keeps the zombie path exactly as it was. The zombie's own gate
                // is `aiState <= 1 || aiState == 5` (`UnitZombie.as:334`), and the port has no separate
                // "caught mid-hesitation" state to test the second arm against, so narrowing this would
                // be a guess. Recorded as an open item rather than silently changed — the robot half
                // (`UnitAIRobot.as:134`) is the one that had a user-visible symptom.
                _sensors.RaiseAlarm(
                    _blackboard, damageSourcePx, durationTicks: 90, caughtUnawares: true);
                BeginDigging();
                return;
            }

            if (_currentState == EnemyAIState.Digging)
            {
                return;
            }

            base.OnDamaged(amount, damageSourcePx);
        }

        /// <summary>
        /// AS3 <c>animate()</c>'s visibility half, which is not an animation choice and so does not
        /// belong in <see cref="ResolveAnimState"/>: <c>aiState == 5</c> sets <c>vis.visible = false</c>
        /// (<c>:265-268</c>) and every other branch sets it back to <c>true</c> (<c>:271</c>, <c>:281</c>).
        /// Driving it from here re-asserts it every tick, so it cannot drift out of step with the state —
        /// and the oracle re-asserts it every frame in exactly the same way.
        ///
        /// <para><b>The order is the oracle's and it matters at the boundary.</b> In <c>animate()</c> the
        /// <c>sost</c> (death) branch is tested <i>before</i> the <c>aiState == 5</c> branch, so a buried
        /// unit that dies becomes a visible corpse instead of staying hidden forever. The equivalent here
        /// is that <see cref="EnemyAIState.Dead"/> is not <c>Buried</c>, so the corpse is shown — and
        /// <c>SetState(Dead)</c> calls this method directly, on the frame it dies.</para>
        /// </summary>
        protected override void UpdateAnimation()
        {
            if (_animator != null)
            {
                _animator.SetVisible(_currentState != EnemyAIState.Buried);
            }

            base.UpdateAnimation();
        }

        /// <summary>
        /// AS3 <c>UnitZombie.as:308</c> — <c>anims["jump"].setStab((dy * 0.6 + 8) / 16)</c>: the
        /// <c>jump</c> row is <c>stab='1'</c> and is <b>posed from the unit's vertical velocity</b>, not
        /// stepped.
        ///
        /// <para><b>Without this the row cannot move at all.</b> <c>zombie0</c>'s <c>jump</c> is
        /// <c>&lt;blit id='jump' y='3' len='16' stab='1'/&gt;</c> (<c>AllData.as:80</c>), and
        /// <c>AnimationFrame.Step</c> returns immediately for a static row — so the animator drew cell 0
        /// and held it for the entire arc. The zombie therefore launched, flew and landed on the same
        /// frozen frame, which is what "his jumping animation seems off compared to the original"
        /// looks like: not a wrong row, a row nobody was driving.</para>
        ///
        /// <para><b>Only <c>jump</c>, and only while airborne.</b> The oracle has exactly one
        /// <c>setStab</c> call site and it is this branch; <c>PoseAnimationState</c>'s own contract is
        /// "pose the row you were handed", so the state test lives here rather than in the base.</para>
        /// </summary>
        protected override void PoseAnimationState(string state)
        {
            if (_animator == null || _controller == null)
            {
                return;
            }

            if (!string.Equals(state, "jump", StringComparison.Ordinal))
            {
                return;
            }

            _animator.SetStab(UnitJumpMath.JumpPoseProgress(_controller.VelocityPixelsPerFrame.y));
        }

        /// <summary>
        /// <c>false</c> — the oracle has no stop-and-swing. <c>UnitZombie.control()</c> keeps
        /// <c>maxSpeed = runSpeed</c> right through the attack window (<c>:714</c>, which is not
        /// conditional on contact) and damages whatever its box happens to overlap. There is no attack
        /// state and no attacker-side cooldown anywhere in the path; the only rate limit is the target's
        /// <c>neujaz</c> window (<c>Unit.as:3273</c>/<c>:4135</c>).
        /// </summary>
        protected override bool StopsToAttack => false;

        /// <summary>
        /// AS3 <c>UnitZombie.as:938</c>:
        /// <c>celUnit &amp;&amp; celDX &lt; optDistAtt &amp;&amp; celDX &gt; -optDistAtt &amp;&amp;
        /// celDY &lt; 80 &amp;&amp; celDY &gt; -80 &amp;&amp; aiState != 5 &amp;&amp; aiState != 6</c>.
        ///
        /// <para>The <c>aiState 5/6</c> exclusions are the digger's buried and digging states. They are
        /// modelled now (see <see cref="PFE.Entities.Enemies.EnemyAIState.Buried"/>), but this test still
        /// does not need them: it is called only from <c>TickCombatChase</c>, and a buried or digging unit
        /// is in neither, so the guard is satisfied by the state machine rather than by a predicate here.
        /// Restating it would be a second copy of a rule that already holds.</para>
        ///
        /// <para>Note what is <b>not</b> in this test: the
        /// box overlap. The oracle tests that separately, inside <c>attKorp</c>
        /// (<c>Unit.as:3273</c>), which is why <c>TryContactAttack</c> re-tests it — the window is a
        /// cheap pre-filter, not the hit test.</para>
        /// </summary>
        protected override bool IsTargetInAttackRange()
        {
            return Mathf.Abs(_blackboard.TargetDeltaX) < _optDistAttack
                && Mathf.Abs(_blackboard.TargetDeltaY) < ContactAttackVerticalPixels;
        }

        protected override void ExecuteAttackAction()
        {
            // AS3 UnitZombie.as:938-950 — `attKorp(celUnit, shok <= 0 ? 1 : 0.5)`.
            //
            // Everything else in that block is super-attack bookkeeping (`aiZlo` accumulation, the
            // `superSilaTip == 7` weapon swing), which needs `setSuper()` and is not modelled — see the
            // class remarks.
            UnitController target = _blackboard.TargetUnit;
            if (target == null || !target.IsAlive)
            {
                return;
            }

            // AS3 passes `1` normally and `0.5` while the unit is still hesitating. `shok` is the
            // oracle's reaction counter (set by `alarma`, `UnitZombie.as:346`); the port carries it as
            // `EnemyBlackboard.ShockTimerTicks`.
            float scale = _blackboard.ShockTimerTicks > 0 ? 0.5f : 1f;

            // Reachable with no controller: `TickCombatChase` faces and range-checks through the
            // blackboard alone, so a brain whose unit has no controller still arrives here. Refusing is
            // not a silent no-op — `EnemyBrain.Initialize` logs a warning for exactly this state — but
            // without it the sim loop would take a NullReferenceException every single tick, which is
            // what it did before this guard existed.
            UnitController attacker = _controller;
            if (attacker == null)
            {
                return;
            }

            bool hit = attacker.TryContactAttack(target, scale);

            // `if(attKorp(...) || isrnd(0.2)) { if(aiZlo > 80) aiZlo -= 25; }` (:940-946) — landing a
            // blow calms the grudge, which is why a zombie that is actually connecting takes longer to
            // reach its super than one that keeps missing.
            if ((hit || UnityEngine.Random.value < 0.2f) && _aiZlo > 80)
            {
                _aiZlo -= 25;
            }

            // `if(superSilaTip == 7 && isrnd(0.1)) currentWeapon.attack();` (:947-950) — the necro
            // variant also bolts its target between supers.
            if (_effectiveSkill == ZombieSuperMath.NecroSpit && UnityEngine.Random.value < 0.1f)
            {
                FireSuperWeapon();
            }
        }

        // ── The super system: state 4 (wind-up) → state 7 (the attack) ─────────────────────────────
        //
        // The oracle's control() is one long if/else chain; these methods are its arms, in its order.
        // The arithmetic is in ZombieSuperMath; what is here is the state, the ordering and the wiring
        // into the port's motor and damage seams.

        /// <summary>
        /// AS3 <c>control()</c> steps 13-15 (<c>UnitZombie.as:723-744</c>) — the grudge meter, the
        /// trigger that starts a super, and the tear-down.
        ///
        /// <para><b>All three run on every live tick, whatever the state.</b> The meter only grows in
        /// the chase, but the trigger's second term is <c>aiState == 3</c> as well as <c>celUnit</c>, and
        /// the tear-down is a test on the <i>previous</i> tick's <c>super_on</c> — so all three have to
        /// be evaluated outside the state dispatch, which is exactly where the oracle puts them: after
        /// the chain and before the movement branch.</para>
        /// </summary>
        private void UpdateSuperState()
        {
            // `if(aiState == 3 && aiZlo < tZlo + 10) ++aiZlo;` (:723-726). The cap is +10 above the
            // threshold, so a zombie that keeps hitting a target without ever firing still saturates.
            if (_currentState == EnemyAIState.CombatChase && _aiZlo < _superTuning.TZlo + 10)
            {
                _aiZlo++;
            }

            // `if(ss > 0 && aiZlo > tZlo && (celUnit || aiState == 3))` (:727-740).
            if (_superSkill > 0
                && _aiZlo > _superTuning.TZlo
                && (_blackboard.TargetUnit != null || _currentState == EnemyAIState.CombatChase))
            {
                if (_teleUnit == null)
                {
                    _teleUnit = _blackboard.TargetUnit;
                }

                // `findSuper()` is called ONLY for skills 1 and 2 (:733-736). Skill 7 shares their
                // pull logic but deliberately starts with no destination — `superSila2` captures one
                // 20 ticks into state 7 instead. Calling it here "for consistency" would change where
                // a necro zombie drags its victim.
                if (_effectiveSkill == ZombieSuperMath.Pounce || _effectiveSkill == ZombieSuperMath.Telekinesis)
                {
                    FindSuper();
                }

                _aiZlo = 0;
                SetState(EnemyAIState.PrepareSuper);
                _blackboard.StateTimerTicks = _superTuning.TPrepSuper;
                return;
            }

            // `if(aiState != 7 && super_on) superSilaVse();` (:741-744) — one tick AFTER state 7 ends,
            // because the state already moved to 3. Running it in the state-7 exit instead would be a
            // tick early; it matters for the pounce, whose velocity would be cleared on the tick the
            // oracle still holds `grav = 0`.
            if (_currentState != EnemyAIState.SuperAttack && _superOn)
            {
                SuperSilaVse();
            }
        }

        /// <summary>
        /// AS3's <c>aiState == 4</c> arm (<c>UnitZombie.as:607-615</c>) — count the wind-up down, then
        /// launch and promote to state 7.
        /// </summary>
        protected override void TickPrepareSuper(int tickIndex)
        {
            if (_superSkill <= 0)
            {
                // Unreachable: only UpdateSuperState enters this state and it tests `ss > 0` first. A
                // guard rather than an assumption, because the failure mode is a zombie frozen in a
                // wind-up for ever, with nothing on screen to say why.
                SetState(EnemyAIState.CombatChase);
                return;
            }

            if (_blackboard.StateTimerTicks > 0)
            {
                _blackboard.StateTimerTicks--;
                return;
            }

            SuperSila();

            SetState(EnemyAIState.SuperAttack);

            // `aiTCh = this.tSuper` AFTER `superSila()`, because the pounce overwrites `tSuper` with a
            // distance-derived value inside it (:1116-1120). Reading it before would give a pounce the
            // table's 20 ticks regardless of how far it had to travel.
            _blackboard.StateTimerTicks = _superTicks;
        }

        /// <summary>
        /// AS3's <c>aiState == 7</c> arm (<c>UnitZombie.as:616-620</c>) plus the per-tick
        /// <c>superSila2()</c> call at <c>:706-709</c>.
        /// </summary>
        protected override void TickSuperAttack(int tickIndex)
        {
            if (_blackboard.StateTimerTicks > 0)
            {
                _blackboard.StateTimerTicks--;
                SuperSila2();
                return;
            }

            SetState(EnemyAIState.CombatChase);

            // `aiTCh = floor(random() * 50) + 50` (:619) — 50..99. `Random.Range(50, 100)` is the
            // integer overload, so the upper bound is exclusive and the range is the oracle's.
            _blackboard.StateTimerTicks = UnityEngine.Random.Range(50, 100);
        }

        /// <summary>
        /// AS3 <c>superSila()</c> (<c>UnitZombie.as:1100-1152</c>) — the moment the attack fires.
        /// </summary>
        private void SuperSila()
        {
            _superOn = true;

            switch (_effectiveSkill)
            {
                case ZombieSuperMath.Pounce:
                    LaunchPounce();
                    break;

                case ZombieSuperMath.VenomSpit:
                case ZombieSuperMath.AcidSpit:
                case ZombieSuperMath.PinkSpit:
                    FireSuperWeapon();
                    break;

                case ZombieSuperMath.Radiation:
                    StartRadiationBurst();
                    break;

                case ZombieSuperMath.Quake:
                    // `loc.budilo(X, Y, 1000)` (:1149) — the only thing superSila does for skill 6;
                    // the shockwaves themselves are superSila2's four-tick beat.
                    Budilo(1000f);
                    break;
            }
        }

        /// <summary>
        /// AS3 <c>superSila2()</c> (<c>UnitZombie.as:1154-1204</c>) — the per-tick half of state 7.
        /// </summary>
        private void SuperSila2()
        {
            switch (_effectiveSkill)
            {
                case ZombieSuperMath.Pounce:
                    // `grav = 0` (:1159-1162). The port has no per-unit gravity override, so it holds
                    // the launch velocity instead — see MaintainPounceVelocity.
                    MaintainPounceVelocity();
                    break;

                case ZombieSuperMath.Telekinesis:
                case ZombieSuperMath.NecroSpit:
                    TickTelekinesis();
                    break;

                case ZombieSuperMath.Radiation:
                    _radCurrent = ZombieSuperMath.RadiationDecay(
                        _radCurrent, _radMax, _radMin, _superTuning.TSuper);
                    break;

                case ZombieSuperMath.Quake:
                    // `if(aiTCh % 4 == 1) quake(12)` (:1197-1203).
                    if (ZombieSuperMath.IsQuakeBeat(_blackboard.StateTimerTicks))
                    {
                        Quake();
                    }
                    break;
            }
        }

        /// <summary>
        /// AS3 <c>superSilaVse()</c> (<c>UnitZombie.as:1206-1227</c>) — put everything back. Called one
        /// tick after state 7 ends, and from death (<c>:351-355</c>).
        /// </summary>
        private void SuperSilaVse()
        {
            _superOn = false;

            switch (_effectiveSkill)
            {
                case ZombieSuperMath.Pounce:
                    // `maxSpeed = vJump + 3; grav = 1;` (:1211-1212). The speed half is the oracle's
                    // post-landing limp; the port's speed ladder re-derives maxSpeed from the state
                    // every tick, so only the velocity reset has an equivalent here.
                    _pounceVelocityX = 0f;
                    _pounceVelocityY = 0f;
                    break;

                case ZombieSuperMath.Telekinesis:
                case ZombieSuperMath.NecroSpit:
                    ClearTelekinetic();
                    break;

                case ZombieSuperMath.Radiation:
                    // `radioactiv = radMin; radrad = radradMin;` (:1222-1226).
                    _radCurrent = _radMin;
                    _radRadius = ZombieSuperMath.RadiationRadiusMin;
                    break;
            }
        }

        /// <summary>
        /// AS3 <c>findSuper()</c> (<c>UnitZombie.as:1039-1098</c>), called at the trigger for skills 1
        /// and 2. Converts into and out of the oracle's coordinate space around the pure search.
        /// </summary>
        private void FindSuper()
        {
            if (_controller == null || _tileQuery == null || _tileQuery.Room == null)
            {
                _superX = -1f;
                _superY = 0f;
                return;
            }

            if (!TryGetSuperCoordinates(out float celX, out float celY, out float selfX, out float selfY))
            {
                _superX = -1f;
                _superY = 0f;
                return;
            }

            RoomInstance room = _tileQuery.Room;
            float roomHeightPx = room.height * TileQueryConstants.TileSize;
            float tileSize = TileQueryConstants.TileSize;
            float bodyHeightPx = UnitHeightPixels;

            ZombieSuperMath.FindSuper(
                _effectiveSkill,
                celX,
                celY,
                selfX,
                selfY,
                bodyHeightPx,
                tileSize,
                (col, as3Row) => IsAirAtAs3Tile(room, col, as3Row),
                out float as3X,
                out float as3Y);

            // `superX` is an X coordinate and is the same in both spaces; only Y flips. The `<= 0`
            // sentinel is preserved rather than converted, or a failed search would become a valid
            // position at the room's bottom edge.
            _superX = as3X;
            _superY = as3X <= 0f ? 0f : ZombieSuperMath.As3YToPortY(as3Y, roomHeightPx);
        }

        /// <summary>
        /// The four numbers <c>findSuper()</c> reads, in AS3's space: the target's centre and this
        /// unit's body centre + feet.
        ///
        /// <para><b>The target's centre comes from the blackboard, not from its transform.</b>
        /// <c>EnemySensors</c> writes <c>TargetDeltaX/Y</c> as <i>target centre − eye</i>, and the eye is
        /// the body centre — so composing them keeps one definition of "where the target is" and works
        /// in <c>Alert</c>, where the target is remembered rather than seen.</para>
        /// </summary>
        private bool TryGetSuperCoordinates(out float celX, out float celY, out float selfX, out float selfY)
        {
            celX = celY = selfX = selfY = 0f;

            Vector2 origin = _tileQuery.OriginPixel;
            float roomHeightPx = _tileQuery.Room.height * TileQueryConstants.TileSize;

            Vector2 feetWorldPx = (Vector2)transform.position * 100f;
            selfX = feetWorldPx.x - origin.x;

            float feetPortY = feetWorldPx.y - origin.y;
            selfY = ZombieSuperMath.PortYToAs3Y(feetPortY, roomHeightPx);

            Vector2 eyePx = GetEyePositionPixels();
            float targetCentrePortY = (eyePx.y + _blackboard.TargetDeltaY) - origin.y;
            celX = (eyePx.x + _blackboard.TargetDeltaX) - origin.x;
            celY = ZombieSuperMath.PortYToAs3Y(targetCentrePortY, roomHeightPx);

            return true;
        }

        /// <summary>
        /// AS3 <c>loc.getTile(col, row).phis == 0</c> — is this tile air? The row is converted from the
        /// oracle's top-down indexing to this port's bottom-up one, and an out-of-bounds tile answers
        /// <c>true</c> because <c>getTile</c> returns the zero-initialised <c>otstoy</c> sentinel there
        /// (<c>Location.as:2324-2331</c>).
        /// </summary>
        private static bool IsAirAtAs3Tile(RoomInstance room, int col, int as3Row)
        {
            int portRow = ZombieSuperMath.As3RowToPortRow(as3Row, room.height);
            TileData tile = room.GetTileAtCoord(new Vector2Int(col, portRow));

            return tile == null || tile.physicsType == TilePhysicsType.Air;
        }

        /// <summary>
        /// AS3 <c>superSila()</c>'s pounce arm (<c>:1107-1123</c>) — launch, and remember the velocity
        /// so <see cref="MaintainPounceVelocity"/> can hold it.
        /// </summary>
        private void LaunchPounce()
        {
            // `if(superX > 0 && superY > 0 && stay)` (:1109). `stay` is the oracle's grounded flag: a
            // pounce from mid-air has nothing to push off.
            if (_controller == null || !_controller.IsGrounded)
            {
                return;
            }

            if (_tileQuery == null || _tileQuery.Room == null)
            {
                return;
            }

            Vector2 origin = _tileQuery.OriginPixel;
            float roomHeightPx = _tileQuery.Room.height * TileQueryConstants.TileSize;

            Vector2 feetWorldPx = (Vector2)transform.position * 100f;
            float selfX = feetWorldPx.x - origin.x;
            float selfPortY = feetWorldPx.y - origin.y;

            if (!ZombieSuperMath.TryPounce(
                    _superX,
                    _superY,
                    selfX,
                    selfPortY,
                    _superTuning.VJump,
                    out float vx,
                    out float vy,
                    out int ticks))
            {
                // The oracle's own outcome when `findSuper()` found no landing column: the wind-up
                // played, state 7 runs for `tSuper` ticks, and nothing happens. Do NOT fall back to the
                // target's position — that would turn a failed search into a different attack.
                return;
            }

            _pounceVelocityX = vx;
            _pounceVelocityY = vy;
            _superTicks = ticks;

            // One absolute assignment now, so the launch is instant as AS3's `dx = …` is, then the
            // per-tick maintenance below keeps it there.
            Vector2 current = _controller.VelocityPixelsPerFrame;
            _controller.AddVelocityPixelsPerFrame(new Vector2(vx - current.x, vy - current.y));

            // The desired speed is what the motor steers toward; setting it stops the motor from
            // braking the launch back toward zero on the very next step.
            MoveHorizontal(vx);
            SetVerticalSpeed(vy);
        }

        /// <summary>
        /// Holds the pounce's velocity for the whole of state 7 — the port's substitute for AS3's
        /// <c>grav = 0</c> (<c>:1159-1162</c>).
        ///
        /// <para><b>Why a correction rather than a flag.</b> Both movement drivers apply gravity and
        /// friction <i>after</i> the brain ticks, and neither exposes a per-unit gravity scale. Adding
        /// back exactly what the step took away each tick reproduces "dy never changes" without
        /// touching the physics layer, and it works identically on the motor and motor-less paths —
        /// which matters because which one is live is a project setting, not a property of this unit.
        /// The residual error within a single step is at most one tick of gravity.</para>
        /// </summary>
        private void MaintainPounceVelocity()
        {
            if (_controller == null) return;

            Vector2 current = _controller.VelocityPixelsPerFrame;
            _controller.AddVelocityPixelsPerFrame(
                new Vector2(_pounceVelocityX - current.x, _pounceVelocityY - current.y));

            MoveHorizontal(_pounceVelocityX);
            SetVerticalSpeed(_pounceVelocityY);
        }

        /// <summary>
        /// AS3 <c>currentWeapon.attack()</c> for the three spit skills (<c>:1126-1129</c>) and the
        /// 10 %-per-contact-tick swing skill 7 adds (<c>:947-950</c>).
        /// </summary>
        private void FireSuperWeapon()
        {
            if (!(_controller is ZombieController zc))
            {
                return;
            }

            // The weapon is a function of the skill, and `WeaponId` already folds ss 9 into ss 1 and
            // returns null for every skill that spits nothing — so a skill with no weapon is a silent
            // no-op here rather than a guard the caller has to repeat.
            string weaponId = ZombieSuperMath.WeaponId(_superSkill);
            if (string.IsNullOrEmpty(weaponId))
            {
                return;
            }

            // `FireOnce`, not `SetFiring(true)`: AS3's `attack()` is a single call, and the mount owns
            // the press/release pair so no cross-tick state is needed here.
            zc.FireSuperWeapon(weaponId, ResolveAimWorldPoint());
        }

        /// <summary>
        /// Where the spit is aimed: the target's body centre when there is one, otherwise the point
        /// ahead of the muzzle. Same shape as <c>ArmedShooterBrain.ResolveAimWorldPoint</c>.
        /// </summary>
        private Vector2 ResolveAimWorldPoint()
        {
            if (_blackboard.TargetUnit != null)
            {
                UnitController target = _blackboard.TargetUnit;
                float height = target.Stats != null ? target.Stats.Height * 100f : 50f;
                return (Vector2)target.transform.position * 100f + new Vector2(0f, height * 0.5f);
            }

            Vector2 eye = GetEyePositionPixels();
            int facing = _controller != null ? _controller.FacingDirection : 1;
            return eye + new Vector2(facing * 100f, 0f);
        }

        /// <summary>
        /// AS3 <c>superSila()</c>'s radiation arm (<c>:1130-1146</c>) — open the aura, then heal every
        /// living zombie inside it.
        /// </summary>
        private void StartRadiationBurst()
        {
            _radCurrent = _radMax;
            _radRadius = ZombieSuperMath.RadiationRadiusMax;

            // The oracle walks `loc.units` and heals every `UnitZombie` with `sost == 1`. The port has
            // no room-owned unit list, so it sweeps the scene the same way
            // `EnemyBrain.RefreshTargetCandidates` does — one definition of "the units in this room",
            // already used by every other cross-unit query.
            Vector2 selfPx = (Vector2)transform.position * 100f;
            float radius = _radRadius;
            float heal = _radHeal;

            var zombies = FindObjectsByType<ZombieController>(FindObjectsSortMode.None);
            for (int i = 0; i < zombies.Length; i++)
            {
                ZombieController other = zombies[i];
                if (other == null || !other.IsAlive || other.Stats == null) continue;

                float distance = Vector2.Distance((Vector2)other.transform.position * 100f, selfPx);
                float amount = ZombieSuperMath.RadiationHeal(heal, radius, distance);
                if (amount > 0f)
                {
                    other.UnitStats.Heal(amount);
                }
            }

            if (_controller != null)
            {
                _controller.EmitParticle("radioblast", selfPx + new Vector2(0f, UnitHeightPixels * 0.5f));
            }

            // NOT ported, and recorded rather than approximated: `ggModum()` damages the PLAYER through
            // `UnitPlayer.raddamage`, which accumulates into the player's `drad`/`rad` stat. The port
            // has no radiation stat at all (PlayerInventory logs `@hrad` as unmodelled), so the damage
            // half of this aura has nowhere to land. Healing the other zombies is the half that does.
        }

        /// <summary>
        /// AS3 <c>superSila2()</c>'s telekinetic arm (<c>:1163-1192</c>).
        /// </summary>
        private void TickTelekinesis()
        {
            // `if(aiTCh == 30) { superX = X; superY = Y; }` (:1165-1168) — capture the anchor 20 ticks
            // into state 7 (tSuper is 50 for these skills), so the victim is dragged to where the
            // zombie stood AFTER it had already closed in.
            if (_blackboard.StateTimerTicks == 30 && _tileQuery != null && _tileQuery.Room != null)
            {
                Vector2 origin = _tileQuery.OriginPixel;
                Vector2 feetWorldPx = (Vector2)transform.position * 100f;
                _superX = feetWorldPx.x - origin.x;
                _superY = feetWorldPx.y - origin.y;
            }

            if (_teleUnit == null || !_teleUnit.IsAlive)
            {
                ClearTelekinetic();
                return;
            }

            // `if(teleUnit && teleUnit.levit != 1)` (:1170). The port's `levit == 1` is a unit that
            // already holds itself up — a flyer or the player's own levitation — and the oracle refuses
            // to grab those.
            if (_teleUnit.IsFlyingUnit || _teleUnit.IsLevitated)
            {
                return;
            }

            if (_tileQuery == null || _tileQuery.Room == null)
            {
                return;
            }

            Vector2 targetOrigin = _tileQuery.OriginPixel;
            Vector2 targetPx = (Vector2)_teleUnit.transform.position * 100f;
            float targetPortX = targetPx.x - targetOrigin.x;
            float targetPortY = targetPx.y - targetOrigin.y;

            ZombieSuperMath.TelekinesisStep(
                _superX,
                _superY,
                targetPortX,
                targetPortY,
                _teleUnit.VelocityPixelsPerFrame,
                out Vector2 delta);

            // `teleUnit.levit = 2` — asserted every tick, exactly as the oracle does, so a target that
            // died and was revived mid-pull is still held.
            _teleUnit.SetLevitated(true);

            if (delta.sqrMagnitude > 0f)
            {
                _teleUnit.AddVelocityPixelsPerFrame(delta);
            }
        }

        /// <summary>
        /// AS3 <c>superSilaVse()</c>'s telekinetic half (<c>:1214-1221</c>) — release the victim.
        /// </summary>
        private void ClearTelekinetic()
        {
            if (_teleUnit != null && _teleUnit.IsLevitated)
            {
                _teleUnit.SetLevitated(false);
            }

            _teleUnit = null;
        }

        /// <summary>
        /// AS3 <c>quake()</c> (<c>UnitZombie.as:526-531</c>) — a shout, a screen shake and a dust
        /// burst, scaled by <c>superQuake</c>.
        ///
        /// <para><b>The wall destruction is not ported.</b> <c>loc.earthQuake</c> shakes the camera and
        /// <c>destroyWall</c> (<c>:486-493</c>) breaks tiles the unit walks into; the port has no
        /// tile-destruction path for units at all — <c>TileData</c> carries hit points but nothing calls
        /// them for an enemy. Recorded as a gap, not silently dropped: a skill-6 zombie here shakes and
        /// shouts but does not open the floor.</para>
        /// </summary>
        private void Quake()
        {
            Budilo(500f);

            if (_controller != null)
            {
                Vector2 feet = (Vector2)transform.position * 100f;
                _controller.EmitParticle("quake", feet + new Vector2(UnityEngine.Random.Range(-20f, 20f), 0f));
            }
        }

        /// <summary>
        /// AS3 <c>animate()</c> (<c>UnitZombie.as:215-320</c>) — the animation state the
        /// oracle would select this frame, reduced to the branches this slice can reach.
        ///
        /// <para><b>The oracle's branch order is the whole point.</b> It tests <c>sost</c> (dead?) first,
        /// then <c>aiState 4/7/5/6</c>, and only then falls through to the movement states — so a corpse
        /// animates as a corpse no matter what <c>dx</c> says, and a digging unit animates as digging no
        /// matter how fast it is "moving". Reordering these would look harmless and would draw the wrong
        /// row.</para>
        /// </summary>
        protected override string ResolveAnimState()
        {
            if (_controller == null)
            {
                return null;
            }

            if (!_controller.IsAlive)
            {
                // AS3 `sost` is 1 alive, 2 dead, 3 dead-with-resurrection-pending. The countdown is now
                // ported (ZombieController), and its last 20 ticks draw the `die` row frame-by-frame
                // with a spark shower (:219-230) — but the port's animator has no seek-to-frame verb, so
                // the row is stepped normally here. The visible difference is one of phase, not of row.
                //
                // UnitZombie.as:234-251. `stay` is AS3's grounded flag, and the oracle reads its own
                // `animState` to decide which of the two to draw — which is exactly the state the
                // animator is currently showing, so the sequence is
                // die -> (airborne) death -> (landed) fall.
                if (_controller.IsGrounded)
                {
                    return _animator != null && string.Equals(_animator.StateName, "death", StringComparison.Ordinal)
                        ? "fall"
                        : "die";
                }

                return "death";
            }

            // AS3 aiState 4 and 7 — `pre` and `super` (`UnitZombie.as:256-264`), tested BEFORE the
            // buried/dig branches because that is the oracle's order. Each is gated on the row existing
            // (`anims["pre"]` / `anims["super"]`): when a variant does not author it the oracle falls
            // through to the movement ladder and the zombie keeps walking, so returning the id
            // unconditionally would freeze it instead.
            if (_currentState == EnemyAIState.PrepareSuper
                && _animator != null && _animator.HasState("pre"))
            {
                return "pre";
            }

            if (_currentState == EnemyAIState.SuperAttack
                && _animator != null && _animator.HasState("super"))
            {
                return "super";
            }

            // AS3 aiState 5 and 6 (UnitZombie.as:265-278), tested HERE — before the grounded and
            // movement branches below — because that is the oracle's order and the order is the point:
            // a digging unit animates as digging no matter how fast it is "moving", and a buried one
            // draws nothing at all whatever `stay` says. Moving these two below the `IsGrounded` check
            // would let a buried unit that is nominally airborne play `jump`.
            if (_currentState == EnemyAIState.Buried)
            {
                // `animate()` sets vis.visible = false and leaves animState untouched, so there is no
                // row to select. Returning null means "no opinion" and UpdateAnimation honours it by not
                // touching the animator, so the state it holds is the one it resumes from on digging
                // out. The hiding itself is UpdateAnimation's job, not this method's.
                return null;
            }

            if (_currentState == EnemyAIState.Digging)
            {
                // `animState = "dig"` (:272). This is a real animation and not a fall-through: `zombie0`
                // carries the `dig` row (row 6, 24 cells) — verified in its asset, and the reason the
                // row is worth naming is that a state whose row is missing is silently invisible here,
                // because UnitAnimator.SetState refuses a state with no frames.
                return "dig";
            }

            // States 4 and 7 are handled above, before the buried/dig branches, because the oracle
            // tests them first. `EnemyAIState.Attack` (the port-only swing) is still never mapped to
            // `super`: AS3's 7 is the super and the port's 7 is a generic swing, which is why the super
            // lives at `SuperAttack` (9) instead.
            if (!_controller.IsGrounded)
            {
                // The pose for this row is written by PoseAnimationState, which the base calls BEFORE it
                // selects the state — AS3's own order (`:308` then `:318`). Returning the id is the whole
                // of this method's job; `jump` being `stab='1'` means no stepping can move it.
                return "jump";
            }

            // UnitZombie.as:276-297. The thresholds are the oracle's and are in AS3's own units —
            // pixels per 30 Hz frame — which is why this reads VelocityPixelsPerFrame and not Velocity.
            float dx = _controller.VelocityPixelsPerFrame.x;

            if (dx < 1f && dx > -1f)
            {
                return "stay";
            }

            if (_currentState == EnemyAIState.CombatChase)
            {
                return "run";
            }

            if (_currentState == EnemyAIState.Alert || dx > 6f || dx < -6f)
            {
                return "trot";
            }

            return "walk";
        }
    }
}
