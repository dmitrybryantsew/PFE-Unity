using PFE.Entities.Units;
using PFE.Systems.Campaign;
using PFE.Systems.Weapons;
using UnityEngine;

namespace PFE.Entities.Enemies
{
    /// <summary>
    /// The brain behind <c>UnitNPC</c> — the port of <c>UnitNPC.control()</c>'s single AI branch
    /// (<c>fe/unit/UnitNPC.as:557-608</c>).
    ///
    /// <para><b>Why an NPC needs a brain at all.</b> <c>UnitNPC</c> extends <c>UnitPon</c> and inherits
    /// the whole <c>Unit</c> AI machinery, but it uses exactly one state: <c>if(aiTip == "agro")</c>.
    /// With no <c>aiTip</c> an NPC does nothing whatsoever — it does not patrol, does not idle-wander and
    /// does not scan for targets. That is not an accident of the data: <c>NPC.as</c>'s personas are
    /// shopkeepers, captives and quest-givers, and an NPC that wandered off its mark would break every
    /// one of them.</para>
    ///
    /// <para><b>What <c>agro</c> means here, exactly.</b> The oracle's branch is three arms and no
    /// state machine (<c>:557-608</c>):</para>
    /// <list type="number">
    /// <item><description>a live <c>celUnit</c> in sight → <c>setCel(celUnit)</c>, turn to face it,
    /// <c>currentWeapon.attack()</c> / <c>dopWeapon.attack()</c>, bubble off, <c>zanyato = true</c>;</description></item>
    /// <item><description>otherwise <c>if(loc.active &amp;&amp; findCel())</c> → the same two flags, having just
    /// acquired a target;</description></item>
    /// <item><description>otherwise → bubble on, <c>zanyato = false</c>, <c>celUnit = null</c>, and a
    /// flyer sinks one pixel a frame.</description></item>
    /// </list>
    /// <para>Then, unconditionally, the floor-contact landing. <b>No <c>dx</c> is written anywhere in
    /// it</b> — an agro NPC does not walk. That is why this brain does <i>not</i> delegate to
    /// <see cref="EnemyBrain.SimTick"/>'s state machine: the base's <c>Idle</c>/<c>Patrol</c> states call
    /// <see cref="EnemyBrain"/>'s own <c>MoveHorizontal</c>, and a Calamity routed through them walked
    /// off the tile the script flew him to (the F3 overlay showed him in <c>Patrol</c>).</para>
    ///
    /// <para><b>Where the landing went.</b> AS3's agro branch ends with
    /// <c>if(turnY &lt; 0) { isFly = false; turnY = 0; aiTip = ""; targNPC.landing(); inter.active =
    /// true; }</c> (<c>:596-607</c>) — a flyer that touches the floor lands and stops fighting. That is
    /// a <i>decision</i> this brain makes and a pair of <i>velocity writes</i> it cannot make, because
    /// <c>As3VelocityPixelsPerFrame</c> is <c>protected</c> on <c>UnitController</c> and this is a
    /// separate <c>MonoBehaviour</c>. So the brain passes the one bit the arm needs
    /// (<c>celUnit != null</c>) to <see cref="UnitNPCController.TickAgroArm"/> every agro tick, and the
    /// controller does the sinking and the landing. Splitting it that way is what lets both halves be
    /// cited against the oracle instead of one being guessed at.</para>
    ///
    /// <para><b>Where the aim point went.</b> AS3 maintains <c>celX</c>/<c>celY</c> in
    /// <c>control()</c> every frame — the <c>if(celUnit == null)</c> block at <c>:528-532</c> plus
    /// <c>setCel()</c> on a sighting — and <c>Weapon.actions()</c> then aims along
    /// <c>atan2(celY - weaponY, celX - weaponX)</c> (<c>Weapon.as:1074-1086</c>). This brain calls
    /// <see cref="UnitNPCController.TickCel"/> for exactly that, in both the agro and the idle path,
    /// because an idle NPC's weapon still follows <c>celX</c>.</para>
    /// </summary>
    public sealed class UnitNpcBrain : EnemyBrain
    {
        /// <summary>Whether <c>aiTip == "agro"</c> — the one state this brain has.</summary>
        public bool IsAgro { get; private set; }

        /// <summary>
        /// AS3 <c>celUnit</c> — the target the agro arm is currently engaging, or <c>null</c>.
        /// </summary>
        /// <remarks>
        /// Exposed because the agro arm's <c>else</c> (the descent) is gated on it and lives on the
        /// controller. Reading it from the blackboard here rather than re-deriving it keeps one
        /// definition of "has a target" — the same value <c>ExecuteAttackAction</c> tests.
        /// </remarks>
        public UnitController CurrentTarget => _blackboard.TargetUnit;

        /// <summary>
        /// AS3's <c>aiTip</c> assignment (<c>UnitNPC.as:400</c>, <c>:206</c>). <c>"agro"</c> turns the
        /// AI on; anything else — including the empty string the <c>ai</c> verb is used with to clear it
        /// — turns it off.
        /// </summary>
        /// <remarks>
        /// <para>Turning <c>agro</c> off pushes a trigger release rather than only clearing the flag, for
        /// the reason <c>ArmedShooterBrain.SimTick</c> documents at length: <c>EnemyWeaponMount</c> latches
        /// <c>_firing</c> until told otherwise, so a unit that stops attacking with the trigger held
        /// keeps shooting while it walks away.</para>
        ///
        /// <para><b><c>SetState</c> is still called, although the state machine is no longer ticked.</b>
        /// It is what the F3 overlay reports and what <c>SetAgro(false)</c> uses to reach
        /// <c>StopMovement</c> through <c>SetState</c>'s <c>Idle</c> arm — the one place the port's
        /// movement intent is zeroed. Nothing reads <c>_currentState</c> to make a decision in this
        /// class, so the value is a label here rather than a mode.</para>
        /// </remarks>
        public void SetAgro(bool agro)
        {
            if (IsAgro == agro)
            {
                return;
            }

            IsAgro = agro;

            if (!agro)
            {
                SetWeaponTrigger(false);
            }

            SetState(agro ? EnemyAIState.CombatChase : EnemyAIState.Idle);
        }

        /// <summary>
        /// Mark this brain's sensors as the NPC kind — <c>UnitNPC.findCel</c>, not
        /// <c>Unit.findCel</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>The oracle overrides <c>findCel</c> on <c>UnitNPC</c>, and the override changes two
        /// rules rather than one.</b> It is
        /// <c>for each(_loc2_ in loc.units) if(look(_loc2_,true)) { setCel(_loc2_); return true; }</c>
        /// (<c>fe/unit/UnitNPC.as:444-460</c>), and the literal <c>true</c> is the second parameter of
        /// <c>look()</c> — the one that suppresses the behind-the-back rejection
        /// (<c>Unit.as:4598</c> <c>if(this.vKonus == 0 &amp;&amp; !param2 &amp;&amp; _loc5_ * storona &lt; 0 …)</c>)
        /// and the vertical-angle penalty (<c>:4589</c>). So an NPC acquires 360°.</para>
        ///
        /// <para>And it commits on a non-zero return, with no <c>obs &gt;= maxObs</c> wait — whereas the
        /// base <c>Unit.findCel</c> waits for the meter on the player branch (<c>:4675</c>) and uses a
        /// <c>&gt; 0.5</c> threshold on the non-player branch (<c>:4693</c>).</para>
        ///
        /// <para>The port shared one rule set between both, which produced exactly the reported
        /// symptom: every raider in the room carried <c>T</c> while <c>Unit_npc</c> carried nothing —
        /// the base was being applied to an NPC whose oracle commits at once and looks behind itself.
        /// Set once here rather than per tick; nothing else in the project writes these fields.</para>
        /// </remarks>
        public override void AttachController(UnitController controller)
        {
            base.AttachController(controller);

            _sensors.Omnidirectional = true;
            _sensors.CommitsWithoutObservation = true;
        }

        /// <summary>
        /// AS3 <c>UnitNPC.control()</c>'s frame, transcribed branch for branch.
        /// </summary>
        /// <remarks>
        /// <para><b>This does not call <c>base.SimTick</c>, and that is the fix rather than an
        /// oversight.</b> The base is a state machine — <c>Idle</c> → <c>Patrol</c> → <c>Alert</c> →
        /// <c>CombatChase</c> — and every one of those states moves the unit. The oracle's NPC has no
        /// state machine at all: <c>aiTip</c> is one string and the only value it acts on is
        /// <c>"agro"</c>. Running the base therefore gave an agro NPC two behaviours the oracle does not
        /// have (an idle wander and a patrol walk) and one it does not want (a chase that closes on a
        /// target the oracle shoots from where it stands). The three pieces the base <i>does</i> own and
        /// this needs — perception, drawing and the weapon step — are called explicitly, in the base's
        /// own order.</para>
        ///
        /// <para><c>UpdateAnimation()</c> is called in every path, because the oracle's <c>animate()</c>
        /// runs unconditionally after <c>control()</c> and it is what plays the NPC's random
        /// <c>move1</c>/<c>move2</c> fidget (<c>UnitNPC.as:243-281</c>) and the <c>fly</c>/<c>stay</c>
        /// pose switch (<c>:262-280</c>). Returning without a draw would freeze an idle NPC on whatever
        /// frame it was built with.</para>
        ///
        /// <para><b><c>TickWeapons()</c> is called in every path too, and leaving it out is what made
        /// Calamity stand weaponless.</b> In the oracle the held weapon is a child object of the unit
        /// with its own <c>step()</c>, and that step runs every frame whatever <c>aiTip</c> says:
        /// <c>Weapon.as:916-925</c> is
        /// <c>this.actions(); if(this.owner) this.owner.setWeaponPos(this.tip); if(vis) this.animate();</c>
        /// — so <c>owner.weaponX/weaponY</c> are rewritten from the body's position every frame, and
        /// <c>Weapon.actions()</c> (<c>:1078-1091</c>) then draws the weapon at them. The port's
        /// equivalent of that per-frame position write is <c>EnemyWeaponMount.Tick</c>, which is reached
        /// only through <see cref="EnemyBrain.TickWeapons"/> — and an idle NPC returned before it. The
        /// mount was therefore never stepped, <c>WeaponRuntimeState.X/Y</c> stayed at their <c>0</c>
        /// initialisers (<c>RangedWeaponController.cs:215-223</c> is the only writer, and only inside
        /// <c>Tick</c>), and <c>WeaponPresenter.ApplyPosition</c> drew the rifle at the world origin
        /// instead of in his hands. The symptom is "Calamity has no weapon"; the gun was equipped and
        /// drawn the whole time, one room away.</para>
        /// </remarks>
        public override void SimTick(int tickIndex)
        {
            // A corpse still animates. The oracle branches on `sost` first, before `aiTip` or `dx`
            // (`UnitZombie.as:234-251`), and the death sequence is three separate rows — so returning
            // without a draw freezes the body on whatever frame it died on.
            if (_currentState == EnemyAIState.Dead)
            {
                UpdateAnimation();
                return;
            }

            // ── AS3's freeze: World.w.enemyAct <= 0 ────────────────────────────────────────────────
            //
            // The gate sits inside `control()`, after the per-frame bookkeeping and BEFORE the
            // `findCel()`/state branch. It returns from `control()` only — the unit's own step calls
            // `control()` in the middle of `forces() … control() … run() … actions() … animate()`
            // (`Unit.as:1806-1840`), so physics, movement, the weapon mounts and the DRAWING all keep
            // running. A frozen NPC still hovers, still animates and still holds its rifle.
            if (EnemyActivity.IsFrozen)
            {
                UpdateAnimation();
                TickWeapons();
                return;
            }

            // The perception half — timers, the grounded mirror, the candidate sweep and
            // `EnemySensors.Evaluate`, which is the port of the oracle's `findCel()`. Hoisted into its
            // own virtual precisely so a brain that replaces the state machine can still acquire a
            // target; see `EnemyBrain.UpdatePerception`.
            UpdatePerception(tickIndex);

            LogPerceptionDiagnostic(tickIndex);

            if (_controller is UnitNPCController npc)
            {
                // AS3 :528-532 plus `setCel()` — the aim point the held weapon reads this frame. Before
                // the trigger, because the mount consumes it in `TickWeapons()` at the end of the tick,
                // and before the arm, because the facing below is derived from it.
                npc.TickCel(_blackboard.TargetUnit);

                // AS3's `ico` — the overhead bubble. Outside `control()` in the oracle (it is a child
                // MovieClip whose frame is set by `NPC.setIco`), but it needs a per-frame owner and this
                // is the only tick an NPC has.
                npc.TickBubble();

                if (IsAgro)
                {
                    // Release last tick's trigger before the arm decides, exactly as ArmedShooterBrain
                    // does: the oracle calls `attack()` once per frame and a frame in which no branch
                    // fires is a frame with the trigger up.
                    SetWeaponTrigger(false);

                    TickAgroCombat(npc);

                    // :594-607 — the agro arm's tail, which is a pair of velocity writes and so lives on
                    // the controller. Run after the decision so it reads THIS frame's target, exactly as
                    // the oracle does (the `else` and the `turnY` test are the last things in
                    // `control()`).
                    npc.TickAgroArm(hasTarget: _blackboard.TargetUnit != null);
                }
            }

            // ...and draw, which the oracle also does last, after `control()`.
            UpdateAnimation();

            // Advance the weapons so a mount told to fire this tick actually produces a projectile.
            // Last in the tick on purpose: everything above is what sets the facing and the aim point,
            // and the weapon has to consume this tick's values rather than the previous tick's.
            TickWeapons();

            // After the weapon step, so the numbers it prints are this tick's rather than last tick's.
            LogFireChainDiagnostic(tickIndex);
        }

        /// <summary>
        /// AS3 <c>UnitNPC.control()</c>'s firing arm (<c>:557-580</c>) — face the target and pull the
        /// trigger.
        /// </summary>
        /// <remarks>
        /// <para><b>There is deliberately no range test here.</b> The oracle's guard is
        /// <c>if(celUnit &amp;&amp; celUnit.sost == 1 &amp;&amp; celUnit.hp > 0)</c> — alive and visible,
        /// nothing about distance — and then it calls <c>currentWeapon.attack()</c> unconditionally. The
        /// base class's <c>IsTargetInAttackRange</c> (a 45 px radius with a documented "no oracle basis,
        /// every archetype should override it") is a <i>melee</i> heuristic; a sniper is not swung, so
        /// applying it here meant Calamity refused to fire at a raider 573 px away — the whole fight, on
        /// a room whose <c>vis='10'</c> makes his sight range 4800 px. The cadence is the weapon's own
        /// <c>t_attack</c> in both the oracle and the port, so pushing the trigger every frame is what
        /// the oracle does rather than a missing throttle.</para>
        ///
        /// <para><b>The facing is derived from <c>celDX</c>, not from the target's raw X.</b> AS3 tests
        /// <c>celDX &gt; 0</c> / <c>celDX &lt; 0</c> against the cel point it just set — which is the
        /// target's box centre shifted a quarter-width along its own facing
        /// (<c>Unit.as:4707-4709</c>). The difference only matters when the two boxes overlap, but it is
        /// free to be exact here because <see cref="UnitNPCController.TickCel"/> has already published
        /// the point.</para>
        ///
        /// <para><b>No <c>dx</c> is written.</b> The oracle's arm has no locomotion at all, so neither
        /// does this: the unit keeps the horizontal intent it had (zero, from <c>SetState(Idle)</c>) and
        /// a flyer keeps the velocity the flight steering left it with.</para>
        /// </remarks>
        void TickAgroCombat(UnitNPCController npc)
        {
            UnitController target = _blackboard.TargetUnit;

            // AS3 :559 — `if(celUnit && celUnit.sost == 1 && celUnit.hp > 0)`. `sost == 1` is "alive and
            // standing"; the port's `IsAlive` is the same predicate. A dead target falls through to the
            // `else` arms in the oracle — which this tick expresses by simply not firing, since the
            // sensors have already cleared `TargetUnit` for a dead candidate.
            if (target == null || !target.IsAlive)
            {
                return;
            }

            // AS3 :562-569 — turn to face. The oracle writes `storona` only when the sign disagrees
            // with the current facing, which is what keeps a unit from re-flipping every frame.
            float celDeltaX = npc.CelPointWorld.x - transform.position.x;
            int direction = celDeltaX >= 0f ? 1 : -1;
            if (direction != _blackboard.FacingDirection)
            {
                _blackboard.FacingDirection = direction;
                ApplyFacing(direction);
            }

            // AS3 :570-577 — `currentWeapon.attack(); dopWeapon.attack();`.
            ExecuteAttackAction();
        }

        /// <summary>
        /// AS3 <c>UnitNPC.control()</c>'s firing arm (<c>:570-577</c>) — <c>currentWeapon.attack()</c> and
        /// <c>dopWeapon.attack()</c>.
        /// </summary>
        /// <remarks>
        /// The oracle reaches this branch only while <c>celUnit</c> is alive and in state <c>1</c>. The
        /// body is the trigger push and nothing else: the aim point is the per-frame <c>celX</c>/<c>celY</c>
        /// that <see cref="UnitNPCController.TickCel"/> publishes, so a shot follows a moving target
        /// without this method having to know where it is.
        /// </remarks>
        protected override void ExecuteAttackAction()
        {
            if (_controller == null || !_controller.IsAlive)
            {
                return;
            }

            if (_blackboard.TargetUnit == null)
            {
                return;
            }

            if (StaggerMath.BlocksAttack(_blackboard.ShockTimerTicks))
            {
                return;
            }

            SetWeaponTrigger(true);
        }

        /// <summary>
        /// Hold or release the persona's weapon, aiming it at the current target. Mirrors
        /// <c>ArmedShooterBrain.SetWeaponTrigger</c>; the difference is that an NPC's weapon comes from
        /// its <i>persona</i> (<c>&lt;npc weap='sniper'/&gt;</c>) rather than from a difficulty roll, so
        /// the controller owns the lookup and this only drives the trigger.
        /// </summary>
        /// <remarks>
        /// <b>There is deliberately no <c>_weaponTriggerHeld</c> memo on this brain.</b>
        /// <c>ArmedShooterBrain</c> declares one (<c>Archetypes/ArmedShooter/ArmedShooterBrain.cs:129</c>)
        /// and writes it at four sites, but never reads it — the authoritative latch is the mount's own
        /// <c>_firing</c>, and <c>EnemyWeaponMount.SetFiring</c> is a bare assignment
        /// (<c>Systems/Weapons/EnemyWeaponMount.cs:337</c>), so pushing the same value twice is free and
        /// idempotent. A memo here would be a second copy of state the mount already holds — the decoy the
        /// project's own rule warns about — so this brain pushes the trigger and keeps nothing.
        /// </remarks>
        void SetWeaponTrigger(bool firing)
        {
            if (!(_controller is UnitNPCController npc))
            {
                return;
            }

            npc.SetWeaponTrigger(firing);
        }

        /// <summary>Tick <see cref="LogPerceptionDiagnostic"/> last printed on, or -1 if never.</summary>
        int _diagnoseTick = -1;

        /// <summary>
        /// Name the gate that refused a target — a diagnostic, not a behaviour.
        /// </summary>
        /// <remarks>
        /// <para>Perception has five independent ways to end a tick with
        /// <c>TargetUnit == null</c> (no candidates, out of range, behind the back, occluded, meter not
        /// filled) and every one of them is identical from the player's chair: "he does not see them".
        /// The F3 overlay reports the outcome, not the reason. This prints the reason, once every
        /// 3 seconds, for an NPC that is standing in a room with something it could be looking at.</para>
        ///
        /// <para>It is quiet for a unit that found its mark, and quiet when the candidate pool is empty
        /// (which is a faction/liveness answer upstream, not a sensor one). Nothing reads it, so it can
        /// be deleted the moment the fight works.</para>
        /// </remarks>
        void LogPerceptionDiagnostic(int tickIndex)
        {
            if (_blackboard.TargetUnit != null)
            {
                return;
            }

            // An empty candidate pool is a faction/liveness answer upstream and not a sensor one — but
            // for an AGRO NPC standing in a room full of raiders it is the answer, and the one gate that
            // could not be named from the console. So the silence applies only while the AI is off.
            if (!IsAgro && _sensors.LastRejection == EnemySensors.RejectionReason.NoCandidates)
            {
                return;
            }

            if (_diagnoseTick >= 0 && tickIndex - _diagnoseTick < 90)
            {
                return;
            }

            _diagnoseTick = tickIndex;

            Debug.Log(
                $"[UnitNpcBrain] '{name}' has no target — refused by {_sensors.LastRejection}, " +
                $"nearest candidate {_sensors.LastRejectionDistancePixels:F0}px away, " +
                $"sight range {_sensors.NeutralSightRangePixels:F0}px " +
                $"(base {_sensors.VisionRangePixels:F0} x visiMult {_sensors.LocationVisiMult}), " +
                $"facing {_blackboard.FacingDirection}, " +
                $"agro={IsAgro}, state={_currentState}.",
                this);
        }

        /// <summary>Tick <see cref="LogFireChainDiagnostic"/> last printed on, or -1 if never.</summary>
        int _fireChainTick = -1;

        /// <summary>
        /// Name the broken link in "he can see them and he does not shoot" — a diagnostic, not a
        /// behaviour.
        /// </summary>
        /// <remarks>
        /// <para><b>Why one line instead of a fix.</b> Between a visible target and a visible bullet
        /// there are six independent links, and every one of them fails <i>silently</i> and looks
        /// identical from the player's chair:</para>
        /// <list type="number">
        /// <item><description><c>IsAgro</c> — the arm only runs while <c>aiTip == "agro"</c>, and
        /// <c>surfCalam</c> gets that from a queued <c>ai agro</c>, not from the placement. Until the
        /// queue drains the unit happily perceives, turns and animates while shooting nothing;</description></item>
        /// <item><description>the persona authored no weapon, or its mount never got built;</description></item>
        /// <item><description>the mount exists but is unarmed — <c>EnemyWeaponMount.Initialize</c>
        /// returns early and unarmed when there is no <c>IProjectileFactory</c>;</description></item>
        /// <item><description>the trigger reaches zero mounts (<c>LastTriggerMountCount</c>);</description></item>
        /// <item><description>the controller refuses: an empty magazine, a reload that cannot complete
        /// without an ammo source, a jam, or <c>shok</c> suppressing the press
        /// (<c>StaggerMath.BlocksAttack</c>);</description></item>
        /// <item><description>a plan is produced and nothing is drawn — a spawn point inside geometry
        /// or a missing visual, which is <c>ShotsFired</c> climbing with no bullet on
        /// screen.</description></item>
        /// </list>
        /// <para>Each of those costs a play-test to distinguish by eye, and the numbers that separate
        /// them are all one read away. Nothing branches on this, so it can be deleted the moment the
        /// fight works.</para>
        ///
        /// <para><b>Gated on a visible target</b> rather than on agro, deliberately: a unit that never
        /// went agro <i>while it can see something</i> is exactly the case that needs naming, and it
        /// would be invisible under an <c>if (IsAgro)</c> guard.</para>
        /// </remarks>
        void LogFireChainDiagnostic(int tickIndex)
        {
            if (_blackboard.TargetUnit == null)
            {
                return;
            }

            if (!(_controller is UnitNPCController npc))
            {
                return;
            }

            if (_fireChainTick >= 0 && tickIndex - _fireChainTick < 60)
            {
                return;
            }

            _fireChainTick = tickIndex;

            EnemyWeaponMount mount = npc.PrimaryWeaponMount;
            string weapon;

            if (mount == null)
            {
                weapon = npc.EquippedWeaponCount == 0
                    ? "none (the persona authored no weap)"
                    : $"NO MOUNT for '{npc.PrimaryWeaponId}' (equip failed — see the EquipWeapon warning)";
            }
            else if (!mount.IsArmed)
            {
                weapon = $"'{mount.Definition?.weaponId}' mount present but UNARMED (no controller)";
            }
            else
            {
                WeaponRuntimeState state = mount.Controller.State;
                int magazine = mount.Definition != null ? mount.Definition.magazineSize : 0;

                weapon =
                    $"'{mount.Definition?.weaponId}' ammo={state.CurrentAmmo}/{magazine} " +
                    $"tAtk={state.TAttack} tRel={state.TReload} jam={state.Jammed} " +
                    $"firing={mount.IsFiring} shots={mount.ShotsFired}";
            }

            Debug.Log(
                $"[UnitNpcBrain] '{name}' sees '{_blackboard.TargetUnit.name}' at " +
                $"{_blackboard.TargetDistance:F0}px — agro={IsAgro} aiTip='{npc.AiTip}' " +
                $"isFly={npc.IsFlyingNow} shok={_blackboard.ShockTimerTicks} | " +
                $"weapon {weapon} | trigger->{npc.LastTriggerMountCount} mount(s) | " +
                $"queue={npc.PendingQueuedVerbCount} wait={npc.QueueWaitFrames} " +
                $"sim={(_simLoop != null ? "ticking" : "NULL")}.",
                this);
        }
    }
}
