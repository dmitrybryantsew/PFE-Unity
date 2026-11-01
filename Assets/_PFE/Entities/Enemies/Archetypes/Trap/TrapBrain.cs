using System;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Campaign;
using PFE.Systems.Combat;
using PFE.Systems.Weapons;
using PFE.Systems.Weapons.Controllers;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    public enum TrapKind
    {
        Turret = 0,
        BearTrap = 1,
        Trigger = 2,
        Damager = 3,
        MagicWall = 4,
        Transmitter = 5,
        Destructible = 6
    }

    /// <summary>
    /// Brain for stationary emplacements, traps, turrets and triggers.
    /// A high-fidelity implementation of AS3 UnitTurret, UnitTrap, UnitTrigger, UnitDamager, UnitMWall, UnitTransmitter, UnitDestr.
    /// Never walks, patrols, or chases.
    /// </summary>
    public class TrapBrain : EnemyBrain
    {
        [SerializeField] protected TrapKind _kind = TrapKind.Turret;
        [SerializeField] protected bool _isArmed = true;
        [SerializeField] protected float _range = 400f;
        [SerializeField] protected float _damage = 35f;

        protected int _fireCooldownTicks;
        protected float _wallDecayAccumulator;

        // ── Barrel watch sweep — UnitTurret.putLoc/control ───────────────────────────────────────────
        //
        // The oracle's aiState 1 is a *watch*: `control()` walks `aRot` one entry every `period`
        // ticks (`:615-621`) and writes the entry to `currentWeapon.forceRot`, while
        // `Weapon.actions()` eases the barrel toward it at `drot` (`Weapon.as:1087-1145`). The port
        // published no force angle at all and its controller snapped the barrel straight to the aim,
        // so a turret's barrel was a rigid pointer with no sweep and no drift — the "it does not
        // rotate and watch the way it did" report.
        //
        // Three pieces of state, one per oracle field: the table (`aRot`), the index (`nRot`) and the
        // countdown (`aiTCh`). Built once, from the placement, because the oracle builds them in
        // `putLoc` before the unit ever ticks.
        protected float[] _sweepTable;
        protected int _sweepIndex;
        protected int _turretTip = -1;

        /// <summary>
        /// The mount's live <c>aiState</c> / <c>aiTCh</c> / <c>aiSpok</c> — <c>UnitTurret</c>'s whole
        /// state, as the pure ladder in <see cref="TurretAIStateMath"/>.
        ///
        /// <para><b>Why a struct and not three fields.</b> The ladder is one machine: a transition reads
        /// all three and writes all three, and splitting them across fields is how the port ended up
        /// with a boolean where the oracle has five states. Keeping it as one value also means the
        /// offline fixtures drive the <i>same</i> object the brain does.</para>
        /// </summary>
        protected TurretAIStateMath.Step _ai = TurretAIStateMath.Step.Initial(false);

        /// <summary>
        /// The mount's <c>hidden</c> field — <c>UnitTurret.as:78-85</c>: 0 open, 1 shutter
        /// (<c>hturret</c>), 2 inert (<c>hturret2</c>). A hidden mount is the only one that ever sits
        /// in <c>aiState 0</c>.
        /// </summary>
        protected int _hidden;

        /// <summary>The watch period in ticks — <see cref="TurretSweepMath.PeriodTicks"/>.</summary>
        protected int _periodTicks = 120;

        /// <summary>Whether the sensors ran a look this tick — the oracle's <c>aiTCh % 10 == 1</c>
        /// gate. Set by <see cref="UpdateSenses"/> and consumed by <see cref="TickTurret"/>, because
        /// the ladder's state promotions live <i>inside</i> that block.</summary>
        protected bool _lookedThisTick;

        public TrapKind Kind { get => _kind; set => _kind = value; }
        public bool IsArmed { get => _isArmed; set => _isArmed = value; }

        /// <summary>
        /// This emplacement has been <b>switched off by a hacked robot terminal</b> — AS3
        /// <c>UnitTurret.sleep</c> (<c>UnitTurret.as:389</c>, set by <c>hack(0)</c>).
        ///
        /// <para><b>Why a separate flag from <see cref="IsArmed"/>.</b> <c>IsArmed</c> is the
        /// bear-trap/trigger latch — a trap that has already sprung. <c>sleep</c> is a turret that is
        /// <i>deliberately</i> inert and can be brought back by a second hack
        /// (<c>UnitTurret.as:394-406</c>). Overloading one flag would make a hacked-off turret
        /// indistinguishable from a spent bear trap, and the two are re-armed differently.</para>
        ///
        /// <para><b>It gates perception as well as firing.</b> AS3's <c>control()</c> opens with
        /// <c>if(World.w.enemyAct &lt;= 0 || this.sleep) return;</c> (<c>UnitTurret.as:572</c>) — before
        /// the cadence-gated <c>findCel()</c> — so a sleeping turret never acquires a target. Gating
        /// only the shot would leave it tracking the player with its barrel, which reads as "not
        /// hacked".</para>
        /// </summary>
        public bool Disabled
        {
            get => _disabled;
            set
            {
                _disabled = value;
                if (!value) return;

                if (_blackboard != null)
                {
                    _blackboard.TargetUnit = null;
                }

                // AS3 `hack(0)` (`UnitTurret.as:385-393`): `sleep = true; aiState = 0;`. The lamp goes
                // dark with it, which is the only in-world signal that a terminal did something.
                _ai = TurretAIStateMath.Step.Initial(hidden: true);

                SetState(EnemyAIState.Idle);
            }
        }

        /// <summary>
        /// The state lamp's frame index — <c>vis.osn.light.gotoAndStop(aiState + 1)</c>
        /// (<c>UnitTurret.as:310-313</c>). See <see cref="EnemyBrain.LampStateIndex"/> for why this
        /// cannot be the generic brain state.
        /// </summary>
        public override int LampStateIndex => _kind == TrapKind.Turret ? _ai.State : base.LampStateIndex;

        [SerializeField] protected bool _disabled;

        protected override bool StopsToAttack => false;

        /// <summary>
        /// Promotes a mounted turret to mobile when knocked off its mount.
        /// <para><b>The citation this method used to carry was wrong.</b> It said
        /// <c>AS3 UnitTurret.otryv()</c>, but <c>otryv</c> exists only on <c>inter/Camera.as</c>
        /// (a camera-lead field) — there is no <c>UnitTurret.otryv</c> anywhere in the oracle. The
        /// method is kept because detaching is a real port behaviour; the invented
        /// <c>MakeNoise(100, true)</c> is removed, since no oracle brain makes noise on detach.</para>
        /// </summary>
        public virtual void DetachFromMount()
        {
        }

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

            if (def.damage > 0) _damage = def.damage;

            string id = def.id != null ? def.id.ToLowerInvariant() : "";
            if (id.Contains("turret")) _kind = TrapKind.Turret;
            else if (id.Contains("trap")) _kind = TrapKind.BearTrap;
            else if (id.Contains("trig")) _kind = TrapKind.Trigger;
            else if (id.Contains("dam")) _kind = TrapKind.Damager;
            else if (id.Contains("wall") || id.Contains("mwall")) _kind = TrapKind.MagicWall;
            else if (id.Contains("transm")) _kind = TrapKind.Transmitter;
            else if (id.Contains("destr")) _kind = TrapKind.Destructible;
        }

        /// <summary>
        /// AS3 <c>UnitTurret.putLoc</c>'s aim setup (`UnitTurret.as:179-269</c>), applied from the
        /// placement the way the oracle reads the placed <c>&lt;obj&gt;</c> node: the mount tip's
        /// <c>aRot</c> table, narrowed by the node's <c>@vis</c>.
        ///
        /// <para><b>Only tips 2 and 5 honour <c>@vis</c>.</b> <c>UnitTurret.as:136-139</c> stores the
        /// attribute for every tip, but the <c>angle</c> cases live inside the
        /// <c>turrettip == 2 || turrettip == 5</c> branch (`:201-231</c>). The shipped rooms author
        /// <c>vis</c> on 2 <c>landturret</c> and 3 <c>cturret</c> placements too — values the oracle
        /// ignores — and honouring them here would aim those turrets differently from the game.</para>
        ///
        /// <para><b>An absent <c>@vis</c> is not "face right".</b> The tip-2 table is
        /// <c>[45,135,-135,-45]</c> and <c>nRot</c> is a random index into it (`:252</c>), so the
        /// initial aim is random — which is exactly the coin flip
        /// <see cref="UnitController.ResolveFacing"/> already resolved at population time for the
        /// facing. Leaving it alone is the faithful arm, and the cone alone is not touched either:
        /// without an aim the AS3 unit still sweeps all four directions in turn, and the port's
        /// hemisphere is the aggregate of any two of them.</para>
        /// </summary>
        public override void OnPlacementApplied(UnitInstance placement)
        {
            base.OnPlacementApplied(placement);
            if (_kind != TrapKind.Turret || placement == null) return;

            int tip = TurretMountMath.TipOfDefinitionId(
                _controller != null && _controller.Definition != null ? _controller.Definition.id : null);

            // ── UnitTurret.putLoc's aRot table (UnitTurret.as:186-253) ─────────────────────────────
            //
            // Built here, once, because that is where the oracle builds it: `putLoc` runs from
            // `setPos` when the room is still inactive, before any `control()` call. The tip is
            // captured too — `fixRot`, `period` and `watchDrot` are all functions of it.
            BuildSweepTable(tip, placement);

            // `UnitTurret.as:144-147` — the placement's `@hidden` overrides the constructor's value, and
            // `:78-85` is where the constructor's came from (`cid='hidden'` / `'hidden2'` on the
            // `hturret` / `hturret2` object rows, which is all `param1` ever carries).
            _hidden = ResolveHidden(
                _controller != null && _controller.Definition != null ? _controller.Definition.id : null,
                placement);

            // `:255-261` — `aiState = 1`, or `0` for a hidden mount, and `nRot` already rolled.
            _ai = TurretAIStateMath.Step.Initial(_hidden > 0);

            if (tip != 2 && tip != 5) return;

            string vis = placement.GetAttribute("vis", null);
            EnemySensors.VisionCone? cone = TurretMountMath.ConeForVis(vis);
            if (cone == null)
            {
                // UnitTurret.as:216-231 has four cases and no else. An unrecognised value is reported
                // rather than silently treated as "no angle", because the oracle would leave the aim at
                // the table default and an authored typo would look identical.
                Debug.LogWarning(
                    $"[TrapBrain] '{name}': placement vis='{vis}' is not one of "
                    + "up/down/left/right (UnitTurret.as:216-231); the mount keeps its aRot default.",
                    this);
                return;
            }

            SetAim(cone.Value, TurretMountMath.FacingForVis(vis, _controller.FacingDirection));
        }

        /// <summary>
        /// The mount's <c>hidden</c> field — <c>UnitTurret.as:78-85</c> (from the object row's
        /// <c>cid</c>) with <c>:144-147</c>'s placement override applied.
        ///
        /// <para><b>The port has no <c>cid</c> channel, so the two shipped carriers are matched by
        /// id.</b> <c>AllData.as:4968-4969</c> declares exactly two: <c>hturret</c> (<c>cid='hidden'</c>)
        /// and <c>hturret2</c> (<c>cid='hidden2'</c>), and 54 room placements use them. Both keep
        /// <c>turrettip = 0</c> — a hidden mount is a floor mount with a shutter, not a seventh kind —
        /// which is why this is a separate field rather than another tip.</para>
        /// </summary>
        protected static int ResolveHidden(string definitionId, UnitInstance placement)
        {
            string authored = placement != null ? placement.GetAttribute("hidden", null) : null;
            if (!string.IsNullOrEmpty(authored) && int.TryParse(authored, out int value))
            {
                return value;
            }

            string id = definitionId != null ? definitionId.ToLowerInvariant() : string.Empty;
            if (id == "hturret2") return 2;
            if (id == "hturret") return 1;
            return 0;
        }

        /// <summary>
        /// Build this mount's <c>aRot</c> table — <c>UnitTurret.putLoc</c>, <c>UnitTurret.as:186-253</c>.
        ///
        /// <para><b>Reads the placement's <c>@vis</c> only for tips 2 and 5</b>, exactly as the oracle
        /// does: <c>:136-139</c> stores the attribute for every tip but the four <c>angle</c> cases live
        /// inside the <c>turrettip == 2 || turrettip == 5</c> branch. The shipped rooms author
        /// <c>vis</c> on some <c>landturret</c>/<c>cturret</c> placements too, and those values are dead
        /// in the original.</para>
        ///
        /// <para><b>Mirroring.</b> <c>:205-210</c> flips a <c>left</c>/<c>right</c> <c>@vis</c> when the
        /// room is mirrored. The port's rooms are pre-mirrored at import, so the attribute already reads
        /// the way the oracle ends up with it and no flip is applied here.</para>
        ///
        /// <para><b>The initial index is random</b> (<c>:248</c>,
        /// <c>nRot = floor(random * aRot.length)</c>) and the initial angle is that entry
        /// (<c>:249</c>). Random rather than 0 is load-bearing for the wall mount, whose four-entry
        /// table would otherwise always start pointing the same diagonal.</para>
        /// </summary>
        protected void BuildSweepTable(int tip, UnitInstance placement)
        {
            _turretTip = tip;

            // `vis` narrows the table only for the wall/ceiling mounts — see the summary above.
            string vis = (tip == 2 || tip == 5) && placement != null
                ? placement.GetAttribute("vis", null)
                : null;

            int facing = _controller != null ? _controller.FacingDirection : 1;
            _sweepTable = TurretSweepMath.SweepTable(tip, vis, facing);
            _sweepIndex = TurretSweepMath.InitialIndex(_sweepTable.Length, UnityEngine.Random.value);
            _periodTicks = TurretSweepMath.PeriodTicks(tip);

            // ── `vKonus = Math.PI / 2` for the wall/ceiling mounts (UnitTurret.as:204, :248) ─────────
            //
            // Only tips 2 and 5 have a real cone, and `look()` reads `vKonus == 0` to decide whether to
            // apply the below-the-eye range penalty (`Unit.as:4589`). Without this a ceiling mount
            // halves its own range for every target under it — i.e. for the floor it was installed to
            // cover. Set here rather than in the `tip == 2 || tip == 5` block below because that block
            // returns early on an unrecognised `@vis` and the cone flag is independent of the aim.
            _sensors.HasElevationCone = tip == 2 || tip == 5;
        }

        /// <summary>
        /// Hand the equipped weapon its sweep and elevation limit — the <c>currentWeapon</c> half of
        /// <c>putLoc</c>.
        ///
        /// <para><b>Why it is not done in <see cref="BuildSweepTable"/>.</b> The table is built from the
        /// placement, but the weapon mount is created by <c>TryEquipTurretWeapon</c>, which can run
        /// <i>before</i> the placement (from <c>Initialize</c>) or <i>after</i> it (from
        /// <c>OnWeaponServicesReady</c>). So the two are set from whichever side arrives second, which
        /// is what this idempotent method is for.</para>
        /// </summary>
        public void ApplySweepToWeapon(EnemyWeaponMount mount)
        {
            if (mount == null || _sweepTable == null || _sweepTable.Length == 0) return;
            if (!(mount.Controller is RangedWeaponController ranged)) return;

            int facing = _controller != null ? _controller.FacingDirection : 1;
            ranged.FixRot = TurretSweepMath.FixRot(_turretTip, facing);

            // The starting angle: AS3 `:249` writes rot = forceRot = aRot[nRot] before the first tick,
            // so the barrel does not sweep in from 0.
            ranged.ForceRot = _sweepTable[_sweepIndex];
            ranged.Drot     = TurretSweepMath.WatchDrot(_turretTip);
        }

        /// <summary>
        /// Point this unit's sensors at one of the four half-planes, and turn the unit to match when
        /// the half has a horizontal side.
        ///
        /// <para><b>Both copies are written.</b> <c>ApplyFacing</c> only reaches the controller; the
        /// blackboard's <c>FacingDirection</c> is what the sensors read and it is written once, in
        /// <c>AttachController</c>/<c>Initialize</c> — before this runs. Writing only the controller
        /// would leave the sensor reading the pre-placement side, which is the same "written but never
        /// read at the right time" shape this project keeps finding.</para>
        /// </summary>
        protected void SetAim(EnemySensors.VisionCone cone, int facing)
        {
            _sensors.Cone = cone;
            if (cone == EnemySensors.VisionCone.Horizontal)
            {
                _blackboard.FacingDirection = facing;
                ApplyFacing(facing);
            }
        }

        public override void SimTick(int tickIndex)
        {
            // ── AS3's freeze gate ───────────────────────────────────────────────────────────────────
            //
            // `UnitTurret.control()` opens with `if(World.w.enemyAct <= 0 || this.sleep) return;`
            // (`UnitTurret.as:572`), before the `aiTCh` decrement at :576 and before the cadence-gated
            // `findCel()` at :638 (`enemyAct > 1 && aiTCh % 10 == 1`). So a frozen emplacement neither
            // acquires a target nor advances its cadence.
            //
            // This override deliberately does NOT call `base.SimTick` — see the perception note below —
            // so it cannot inherit the gate that lives there. It has to be repeated here.
            //
            // What still runs: the oracle's return is from `control()` only. `forces()`, `run()`,
            // `actions()` and `animate()` are all outside it (`Unit.as:1806-1840`), so the emplacement
            // keeps drawing and its weapon mount keeps advancing — which is why `TickWeapons()` is
            // called here exactly as the live path's own tail calls it.
            if (EnemyActivity.IsFrozen)
            {
                TickWeapons();
                return;
            }

            // ── A hacked-off turret ────────────────────────────────────────────────────────────────
            //
            // AS3 `if(World.w.enemyAct <= 0 || this.sleep) return;` (UnitTurret.as:572) — the return is
            // from control() only, so the mount keeps advancing and the sprite keeps drawing. That is
            // why TickWeapons() still runs and why the trigger is released explicitly: a turret that
            // was firing when it was hacked must stop, not finish its burst.
            if (_disabled)
            {
                if (_controller is TurretController hackedTurret && hackedTurret.TurretWeaponMount != null)
                {
                    hackedTurret.TurretWeaponMount.SetFiring(false);
                }

                StopMovement();
                TickWeapons();
                return;
            }

            if (_fireCooldownTicks > 0) _fireCooldownTicks--;

            // Stationary emplacements never run motor locomotion
            StopMovement();

            if (_controller == null || !_controller.IsAlive)
            {
                return;
            }

            // ── Perception, BEFORE the family switch ──────────────────────────────────────────────
            //
            // AS3's traps are not exempt from the base perception entry: `UnitTurret.control()` runs
            // the shared `findCel()` on the same tick as its own branch (`UnitTurret.as:648</c>), and
            // `UnitTrap`/`UnitTrigger`/`UnitDamager`/`UnitMWall` are all ordinary `Unit`s that call
            // `super.control()`. This override used to jump straight to its own switch, so
            // `EnemySensors.Evaluate` — the ONLY writer of `EnemyBlackboard.TargetUnit` in the whole
            // project — never ran for ANY trap-family unit, and every
            // `if (_blackboard.TargetUnit == null) return;` below was unconditionally true. That is
            // the reported "turrets never target the player and never attack": not per unit, per family.
            UpdatePerception(tickIndex);

            switch (_kind)
            {
                case TrapKind.MagicWall:
                    TickMagicWall();
                    break;
                case TrapKind.Transmitter:
                    TickTransmitter(tickIndex);
                    break;
                case TrapKind.BearTrap:
                    TickBearTrap(tickIndex);
                    break;
                case TrapKind.Turret:
                    TickTurret(tickIndex);
                    break;
                case TrapKind.Trigger:
                    TickTrigger(tickIndex);
                    break;
                case TrapKind.Damager:
                    TickDamager(tickIndex);
                    break;
                case TrapKind.Destructible:
                    break;
            }

            // Advance whatever the controller equipped, last, so the aim this tick published is the
            // one the weapon consumes — the same order the base ends its own tick with.
            TickWeapons();
        }

        // ── Perception cadence ───────────────────────────────────────────────────────────────────────
        //
        // An emplacement in AS3 looks once every ten ticks, not every tick:
        //
        //    if(World.w.enemyAct > 1 && aiTCh % 10 == 1)
        //    {
        //       ...
        //       if(!stun && this.findCel())
        //
        // (`UnitTurret.as:638`). `findCel` is where the candidate list is built *and* where the line of
        // sight is traced, so one gate throttles both — which is exactly what the oracle does.
        //
        // The port ran it every tick. Measured 2026-10-27: nine traps at 0.516 ms each were 4.65 ms of a
        // 9.64 ms tick — 48 % of the entire dispatch loop — for emplacements whose own logic is a handful
        // of comparisons. The cost was never trap logic; it was unit perception running at 30 Hz on
        // something that has no business looking that often. See docs/Perf_FpsLoss_Plan_2026-10-27.md §9.
        //
        // WHY THE PHASE IS `RaycastPhase` AND NOT `0`. EnemySensors.Evaluate traces the line of sight only
        // on `tickIndex % RaycastThrottleInterval == RaycastPhase` (`EnemySensors.cs`). A trap that looked
        // on any other phase would evaluate with `shouldRaycast` false on every one of its looks, so it
        // would never establish line of sight and never acquire a target — a silent behavioural
        // regression with no error and no failing test. Both the interval and the phase are read off the
        // sensor rather than restated here so the two cannot drift apart.
        //
        // `UpdateSenses` and not `UpdatePerception`: the timer countdowns in the base run every tick in
        // AS3 too, and throttling them would make a stun last ten times longer.
        protected override void UpdateSenses(int tickIndex)
        {
            int cadence = _sensors.RaycastThrottleInterval;

            _lookedThisTick = cadence <= 1 || tickIndex % cadence == EnemySensors.RaycastPhase;

            if (!_lookedThisTick)
            {
                return;
            }

            base.UpdateSenses(tickIndex);
        }

        protected virtual void TickMagicWall()
        {
            // UnitMWall.as: hp -= 0.2 per tick; exterminate at 0
            _wallDecayAccumulator += 0.2f;
            if (_wallDecayAccumulator >= 1f)
            {
                int dmg = (int)_wallDecayAccumulator;
                _wallDecayAccumulator -= dmg;
                var pending = PendingDamage.Direct(
                    DamageContext.Contact(dmg, DamageType.PhysicalMelee),
                    _controller,
                    transform.position);
                if (_controller.DamageSystem != null)
                {
                    _controller.DamageSystem.Report(pending);
                }
            }
        }

        protected virtual void TickTransmitter(int tickIndex)
        {
            // UnitTransmitter.as: damage every 30 ticks within radius
            if (tickIndex % 30 == 1 && _blackboard.TargetUnit != null)
            {
                float dist = _blackboard.TargetDistance;
                if (dist <= 400f) // distdam 400
                {
                    float factor = 1f - (dist / 400f);
                    float dmg = Mathf.Max(1f, _damage * factor);
                    var pending = PendingDamage.Direct(
                        DamageContext.Contact(dmg, DamageType.Acid),
                        _blackboard.TargetUnit,
                        transform.position);
                    _controller.DamageSystem?.Report(pending);
                }
            }
        }

        protected virtual void TickBearTrap(int tickIndex)
        {
            if (!_isArmed) return;

            // UnitTrap.as: proximity overlap -> damage(35, D_INSIDE) once
            if (_blackboard.TargetUnit != null && _blackboard.TargetDistance <= 30f)
            {
                _isArmed = false; // Sprung
                var pending = PendingDamage.Direct(
                    DamageContext.Contact(35f, DamageType.PhysicalMelee),
                    _blackboard.TargetUnit,
                    transform.position);
                _controller.DamageSystem?.Report(pending);
            }
        }

        /// <summary>
        /// AS3 <c>UnitTurret.control()</c> — <c>UnitTurret.as:562-745</c>.
        ///
        /// <para><b>The weapon is a real Weapon, not a damage call.</b> The oracle's constructor is
        /// <c>currentWeapon = new Weapon(this, "turret" + this.tr)</c> (`UnitTurret.as:126-127</c>)
        /// with <c>auto = true</c> (`:130</c>), and the tick's only attack is
        /// <c>currentWeapon.attack()</c> when the mount is in <c>aiState 3</c> (`:737-740</c>).
        /// The port's equivalent is the mount the controller equipped: aim it at the target and hold
        /// the trigger while the ladder says "attacking", and the mount's own cadence decides the
        /// shots.</para>
        ///
        /// <para><b>The five-state ladder replaced a boolean, and the boolean was the bug.</b> The
        /// port had "committed / not committed" and fired on commit, so a mount went from silent to
        /// shooting in one tick — no orange warning stage, no red stage distinct from the first shot,
        /// and no <c>vision = 0.35</c> when dormant. See <see cref="TurretAIStateMath"/> for the ladder
        /// and the lamp frame each state lights.</para>
        ///
        /// <para><b>The invented 400 px engagement radius is gone.</b> <c>_range</c> used to gate both
        /// the aim and the trigger, with no counterpart in the oracle: <c>aiState == 3</c> fires, and
        /// what put the mount in 3 was <c>findCel()</c> — the sensor's own range, cone and tile ray.
        /// A second, shorter radius on top of that meant a turret that had already acquired you kept
        /// sweeping its barrel and holding fire until you were inside 10 tiles, which is most of why
        /// it read as blind.</para>
        ///
        /// <para><b>The trigger is released before the target test, not in an else.</b> AS3's
        /// <c>attack()</c> is one call per frame that decides from scratch, so a frame in which no
        /// branch fires is a frame with the trigger up. <c>EnemyWeaponMount</c> latches
        /// <c>_firing</c> until told otherwise, and <c>TickWeaponMounts</c> runs at the end of this
        /// tick — a turret that loses its target would keep shooting at where it was.</para>
        /// </summary>
        protected virtual void TickTurret(int tickIndex)
        {
            EnemyWeaponMount mount = _controller is TurretController turretController
                ? turretController.TurretWeaponMount
                : null;

            if (mount != null)
            {
                mount.SetFiring(false);
            }

            UnitController target = _blackboard.TargetUnit;

            // `findCel()`'s answer as the oracle stores it: `celUnit != null`. NOT a distance test —
            // `setCel` is what committed it, and the sensor already applied the range, the cone and the
            // tile ray when it did.
            bool hasTarget = target != null && _blackboard.HasLineOfSight;

            // ── The ladder ───────────────────────────────────────────────────────────────────────────
            //
            // One call covers both of `control()`'s blocks in the oracle's order: the cadence chain
            // (`:576-629`, every tick) and the perception block (`:638-674`, on `aiTCh % 10 == 1`).
            _ai = TurretAIStateMath.Advance(
                _ai, hasTarget, _hidden, _periodTicks, UnityEngine.Random.value, _lookedThisTick);

            if (_ai.AdvanceSweep && _sweepTable != null && _sweepTable.Length > 0)
            {
                _sweepIndex = TurretSweepMath.NextIndex(_sweepIndex, _sweepTable.Length);
            }

            if (_ai.RangAlarm)
            {
                // `budilo(750)` — UnitTurret.as:586. The room's alarm is what makes one turret spotting
                // you bring the neighbours.
                Budilo(750f);
            }

            // ── What the state does to perception ────────────────────────────────────────────────────
            //
            // `UnitTurret.as:692-735` writes `vision` and `overLook` per state. `overLook` is `look()`'s
            // `param2`, i.e. "ignore my cone" — true while dormant and true for every state from 2 up, so
            // a mount that has engaged you tracks you through 360°.
            _sensors.VisionPower = TurretAIStateMath.VisionScale(_ai.State, _hidden);
            _sensors.Omnidirectional = TurretAIStateMath.Omnidirectional(_ai.State);

            // ── The barrel ───────────────────────────────────────────────────────────────────────────
            //
            // `Weapon.actions()` reads `findCel` and `drot` (Weapon.as:1087-1092): with `findCel` clear
            // it takes `rot2 = forceRot`; with it set it aims at `celX/celY`. Publishing both from one
            // place keeps the drawn barrel and the fired round on the same angle — the controller
            // consumes them, and the presenter draws the controller's `State.Rot`.
            RangedWeaponController ranged = mount != null ? mount.Controller as RangedWeaponController : null;
            bool aimsAtTarget = TurretAIStateMath.AimsAtTarget(_ai.State) && hasTarget;

            if (ranged != null && _sweepTable != null && _sweepTable.Length > 0)
            {
                ranged.Drot = TurretAIStateMath.Drot(_ai.State, _turretTip);

                if (aimsAtTarget)
                {
                    // `findCel = true` — the barrel aims at `celX/celY`, so the sweep value is unused.
                    ranged.ForceRot = null;
                }
                else
                {
                    // `findCel = false` — the barrel eases toward `forceRot`. A dormant mount parks it
                    // straight down (`:227`, `:599`); a watching one walks the table.
                    ranged.ForceRot = _ai.State == TurretAIStateMath.Dormant
                        ? TurretSweepMath.HiddenParkedRot
                        : _sweepTable[_sweepIndex];
                }
            }

            if (target == null) return;

            if (aimsAtTarget)
            {
                // Face target (AS3 `storona = celDX > 0 ? 1 : -1`, UnitTurret.as:720). Both copies are
                // written — see SetAim.
                //
                // **Except the arm mount**, which sets `noTurn = true` in putLoc (`:245`) and therefore
                // never runs that line: `:718-720` is `if(!this.noTurn) storona = …`. The arm turret is
                // bolted to a wall bracket and can only traverse ±30° from the bracket's side, so
                // turning its body to face the player would let it shoot through the wall it is
                // mounted on — and would also invalidate the `fixRot` 2/3 elevation limit, which is
                // chosen from the facing it is no longer allowed to change.
                bool noTurn = _turretTip == 3;
                if (!noTurn)
                {
                    float dx = _blackboard.TargetDeltaX;
                    if (dx > 5f)
                    {
                        _blackboard.FacingDirection = 1;
                        ApplyFacing(1);
                    }
                    else if (dx < -5f)
                    {
                        _blackboard.FacingDirection = -1;
                        ApplyFacing(-1);
                    }
                }

                if (mount != null)
                {
                    // ── The target's MID-BODY, not its origin ────────────────────────────────────────
                    //
                    // AS3 `Unit.setCel` (:4707-4708) is `celX = X + scX/4 * storona`,
                    // `celY = Y - scY/2` — a quarter body along the target's facing and half a body
                    // up. A ported unit's transform sits on its FEET (the room anchor is the AS3
                    // `Y`), so aiming at `transform.position` points the barrel half a body too low.
                    //
                    // That is the reported "turrets really struggle to fire its projectiles at the
                    // player, something must be eating them": the round is fired from the barrel on a
                    // line to the player's feet, so on flat ground it clips the floor a metre short,
                    // and against a wall turret it buries itself in the wall's own ledge. Nothing was
                    // eating the projectile — it was aimed at the ground.
                    //
                    // `UnitTurret` has no `cel` of its own: it calls `setCel(World.w.gg)` (:531), so
                    // the turret takes the full point INCLUDING the quarter-width lead.
                    mount.SetAimTarget(EnemySensors.GetUnitAimWorld(target));

                    // `if(World.w.enemyAct >= 3 && aiState == 3) currentWeapon.attack()` — `:737-740`.
                    // A spotted mount (state 2) tracks but holds fire; that is the orange stage.
                    mount.SetFiring(TurretAIStateMath.Fires(_ai.State));
                }
                else if (TurretAIStateMath.Fires(_ai.State) && _fireCooldownTicks <= 0)
                {
                    // No mount: a bare test spawn, or one whose weapon services never arrived. AS3's
                    // `currentWeapon.attack()` still happens, so the damage still lands — the direct
                    // hit is the fallback, not the mechanism.
                    ExecuteAttackAction();
                    _fireCooldownTicks = 30; // ~1 attack per second cadence
                }
            }

            // `attKorp(celUnit, 1)` — UnitTurret.as:741-744, the point-blank contact hit, which is
            // independent of the weapon and fires on every tick a target is in contact range.
            if (_damage > 0 && _blackboard.TargetDistance <= 50f)
            {
                _controller.TryContactAttack(target, 1f);
            }
        }

        protected virtual void TickTrigger(int tickIndex)
        {
            if (!_isArmed) return;

            if (_blackboard.TargetUnit != null && _blackboard.TargetDistance <= 50f)
            {
                _isArmed = false; // One-shot latch
                Budilo(1200f);
            }
        }

        protected virtual void TickDamager(int tickIndex)
        {
            if (!_isArmed) return;

            if (_fireCooldownTicks <= 0 && _blackboard.TargetUnit != null && _blackboard.TargetDistance <= _range)
            {
                ExecuteAttackAction();
                _fireCooldownTicks = 45;
            }
        }

        protected override bool IsTargetInAttackRange()
        {
            if (_blackboard.TargetUnit == null) return false;

            // A turret's reach IS its sight: the oracle has no second radius, so `aiState 3` fires
            // wherever `findCel()` committed. `_range` stays the answer for the damager, which has its
            // own `distdam` in the oracle and is not this rule.
            if (_kind == TrapKind.Turret) return true;

            return _blackboard.TargetDistance <= _range;
        }

        /// <summary>
        /// <c>UnitTurret.alarma()</c> (<c>UnitTurret.as:343-362</c>) — a mount that is shot at or hears a
        /// neighbour jumps <b>straight to <c>aiState 3</c></b> with a full awareness budget, skipping the
        /// 30..39-tick spotted delay. The base class promotes <c>Idle</c>/<c>Patrol</c> to
        /// <c>Alert</c>, which for a turret only ever moved the generic enum and left the ladder — and
        /// therefore the lamp — where it was.
        /// </summary>
        public override void OnDamaged(float amount, Vector2 damageSourcePx)
        {
            base.OnDamaged(amount, damageSourcePx);

            if (_kind == TrapKind.Turret && !_disabled && _controller != null && _controller.IsAlive)
            {
                _ai = TurretAIStateMath.Alarm(_ai);
            }
        }

        protected override void ExecuteAttackAction()
        {
            if (_controller == null || !_controller.IsAlive || _blackboard.TargetUnit == null) return;

            // Ranged direct attack or contact attack
            Vector2 targetPos = (Vector2)_blackboard.TargetUnit.transform.position * 100f;
            var pending = PendingDamage.Direct(
                DamageContext.Contact(_damage > 0 ? _damage : 15f, DamageType.PhysicalBullet),
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

        protected override string ResolveAnimState()
        {
            // Flash vector MovieClips / no blit rows (guide 18 §7)
            return null;
        }
    }
}
