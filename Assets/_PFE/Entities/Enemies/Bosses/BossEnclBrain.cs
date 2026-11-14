using System;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Campaign;
using PFE.Systems.Combat;
using PFE.Systems.Map;
using PFE.Systems.Map.Actions;
using PFE.Systems.Weapons;
using UnityEngine;

namespace PFE.Entities.Enemies.Bosses
{
    /// <summary>
    /// The Enclave Commander — AS3 <c>UnitBossEncl</c> (<c>UnitBossEncl.as</c>).
    ///
    /// <para><b>This is three bosses wearing one name.</b> The arena spawns the unit three times with
    /// <c>tr = 1/2/3</c> (<c>RoomsProb.as:896-898</c>; the port's <c>rooms_prob/bossencl.asset</c>
    /// carries the three placements with the <c>tr</c> attribute). Each <c>tr</c> is a different body,
    /// a different weapon and a different set of resistances, and all three share one firing right that
    /// rotates between them. There is <b>no phase table</b> — the encounter's shape is that rotation,
    /// not HP thresholds.</para>
    ///
    /// <para><b>Why the brain overrides <see cref="EnemyBrain.SimTick"/> instead of driving the base
    /// state machine.</b> <c>UnitBossEncl.control()</c> (<c>:212-299</c>) has no state ladder at all: it
    /// decrements two timers, polls a target every 40 ticks, runs one flight block and calls
    /// <c>attack()</c>. Feeding that through patrol/chase/attack would invent a pursuit the oracle never
    /// had — this unit never walks and never chases; it patrols a fixed box. The split used here is the
    /// one <see cref="BossRaiderBrain"/> established: keep the base's perception, weapon and animation
    /// services, and replace the decision body.</para>
    /// </summary>
    public class BossEnclBrain : BossBrain
    {
        /// <summary>
        /// <c>public var tr:int = 1</c> (<c>:11</c>) — which of the three commanders this body is.
        ///
        /// <para><b>Read from the placement, not from the asset.</b> The unit row is shared by all
        /// three; only the placement's <c>tr</c> attribute distinguishes them (<c>:43-45</c>
        /// <c>if(param3.@tr.length()) this.tr = param3.@tr;</c>).</para>
        /// </summary>
        [SerializeField] protected int _commanderIndex = BossEnclRules.DefaultCommander;

        // ── control()'s state (UnitBossEncl.as:212-299) ─────────────────────────────────────────

        /// <summary><c>aiTCh</c> — the countdown that ends the spawn hover and then re-rolls forever.</summary>
        protected int _aiTicks = BossEnclRules.AiTicksInitial;

        /// <summary>
        /// <c>aiState</c> — <c>0</c> during the spawn hover and while stunned, <c>1</c> otherwise.
        ///
        /// <para><b>It never takes a third value.</b> <c>control()</c>'s else arm assigns
        /// <c>aiState = 1</c> unconditionally (<c>:242</c>), so after the first 30 ticks the commander
        /// is in state 1 for the rest of its life. A port that modelled this as a patrol/chase/attack
        /// ladder would be inventing transitions.</para>
        /// </summary>
        protected int _aiState;

        /// <summary>The flight state, in AS3's orientation — see <see cref="BossEnclRules.BossEnclFlight"/>.</summary>
        protected BossEnclRules.BossEnclFlight _flight;

        /// <summary>Whether <c>sinX</c>/<c>sinDX</c> have been drawn (the oracle draws them in field initialisers).</summary>
        protected bool _flightSeeded;

        /// <summary>
        /// <c>celUnit</c> — AS3's target, re-seated only on the <c>aiTCh % 40 == 1</c> poll
        /// (<c>:245-255</c>).
        ///
        /// <para><b>Why this is not just <c>_blackboard.TargetUnit</c>.</b> The port's perception
        /// re-picks a target every tick; the oracle re-picks every 40. The difference is observable: a
        /// commander that has just been handed a target keeps shooting <i>that</i> one for up to 40 ticks
        /// even if a closer unit walks past. Holding the poll's answer is what reproduces it.</para>
        /// </summary>
        protected UnitController _celUnit;

        /// <summary>The room's shared rotation (AS3 <c>Location.unitCoord</c>).</summary>
        protected BossEnclCoord _coord;

        public int CommanderIndex
        {
            get => _commanderIndex;
            set => _commanderIndex = value;
        }

        /// <summary>The shared rotation, or <c>null</c> until the first tick that can resolve a room.</summary>
        public BossEnclCoord Coord => _coord;

        /// <summary><c>StopsToAttack => false</c> — the commander never plants its feet; it fires on the move.</summary>
        protected override bool StopsToAttack => false;

        /// <summary>This brain as its controller, which is where the per-<c>tr</c> loadout lives.</summary>
        protected BossEnclController EnclController => _controller as BossEnclController;

        protected override void Awake()
        {
            base.Awake();
            SeedFlight();
        }

        public override void OnDefinitionAssigned()
        {
            base.OnDefinitionAssigned();
            SeedFlight();
        }

        /// <summary>
        /// The constructor's two random draws — <c>sinX = Math.random() * 10</c> and
        /// <c>sinDX = Math.random() * 0.1 + 0.02</c> (<c>:33-35</c>).
        ///
        /// <para><b>Drawn once, and only once.</b> They are field initialisers in the oracle, so every
        /// commander gets its own phase and its own drift rate at construction, and neither ever changes
        /// afterwards. Re-drawing them on a definition change would make the boss's path depend on
        /// load order.</para>
        /// </summary>
        void SeedFlight()
        {
            if (_flightSeeded) return;

            _flight.SinX = UnityEngine.Random.value * BossEnclRules.SinPhaseRange;
            _flight.SinDelta = UnityEngine.Random.value * BossEnclRules.SinDeltaRange + BossEnclRules.SinDeltaBase;
            _flight.VerticalDirection = BossEnclRules.AiVerticalDirectionInitial;
            _flightSeeded = true;
        }

        /// <summary>
        /// The placement half of the constructor — <c>if(param3.@tr.length()) this.tr = param3.@tr;</c>
        /// (<c>:43-45</c>) — then the per-<c>tr</c> loadout.
        ///
        /// <para><b>The loadout has to be applied here and not only in the controller.</b> <c>tr</c>
        /// arrives with the placement, which is applied after <c>Initialize</c>; the controller cannot
        /// know which weapon, armour pool or resistance set to build before this method has run. The
        /// controller's apply is idempotent, so the <c>Initialize</c> pass and this one cannot compound.
        /// </para>
        /// </summary>
        public override void OnPlacementApplied(UnitInstance placement)
        {
            base.OnPlacementApplied(placement);

            string tr = placement?.GetAttribute("tr", null);
            if (!string.IsNullOrEmpty(tr) && int.TryParse(tr, out int parsed) && parsed > 0)
            {
                _commanderIndex = parsed;
            }

            EnclController?.ApplyCommanderLoadout();
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        //  The tick
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>control()</c> (<c>:212-299</c>) plus the two things the base tick did around it.
        ///
        /// <para><b>The shared <c>Coord</c> is stepped before this commander's own body</b>, which is
        /// the oracle's order: <c>Location.step()</c> runs the coord (<c>Location.as:3388-3390</c>) and
        /// the units' <c>control()</c> runs afterwards. Only the first commander to tick in a frame
        /// advances it (<see cref="BossEnclCoord.StepIfDue"/>), so three bodies sharing one timer still
        /// produce one decrement per frame.</para>
        /// </summary>
        public override void SimTick(int tickIndex)
        {
            if (_controller == null) return;

            // `if(sost == 3) return;` — a corpse still draws, which is the port's convention. The unit
            // authors no `die` row (only `stay`/`fly`), so `ResolveAnimState` holds it on `stay`.
            if (!_controller.IsAlive)
            {
                UpdateAnimation();
                return;
            }

            // `if(World.w.enemyAct <= 0) { celY = ...; celX = ...; return; }` (<c>:230-235>). The boss
            // stops deciding but keeps drawing and keeps its weapon mount stepping.
            if (EnemyActivity.IsFrozen)
            {
                UpdateAnimation();
                TickWeapons();
                return;
            }

            TickCoord(tickIndex);

            UpdatePerception(tickIndex);
            TickControl(tickIndex);
            UpdateAnimation();
            TickWeapons();
        }

        /// <summary>
        /// Resolve the room's <see cref="BossEnclCoord"/> and advance it once per tick — AS3
        /// <c>Location.step()</c>'s <c>unitCoord.step()</c> plus the two <c>allAct</c> broadcasts.
        /// </summary>
        protected virtual void TickCoord(int tickIndex)
        {
            if (!EnsureCoord()) return;

            BossEnclCoordEvents events = _coord.StepIfDue(tickIndex);
            if (events == BossEnclCoordEvents.None) return;

            RoomInstance room = CurrentRoom;
            ObjectInteractionBroadcast broadcast = ObjectInteractionBroadcast.Current;
            if (room == null || broadcast == null) return;

            // `while(i <= kolAll) { loc.allAct(null, opened[i] ? "red" : "green", "a" + i); i++; }`
            // (:80-88) and the same loop with "open"/"close" at :89-99. `allAct` with a null sender is
            // the port's `RunFromScript`.
            for (int i = 0; i < BossEnclRules.DoorCount; i++)
            {
                if ((events & BossEnclCoordEvents.SignalDoors) != 0)
                {
                    broadcast.RunFromScript(room, _coord.DoorSignalVerb(i), BossEnclCoord.DoorTag(i));
                }

                if ((events & BossEnclCoordEvents.ToggleDoors) != 0)
                {
                    broadcast.RunFromScript(room, _coord.DoorCommandVerb(i), BossEnclCoord.DoorTag(i));
                }
            }
        }

        /// <summary>
        /// Find this commander's room and take (or create) the rotation hanging on it — AS3
        /// <c>putLoc</c>'s <c>if(param1.unitCoord == null) param1.unitCoord = new Coord(param1);</c>
        /// plus <c>this.coord["liv"+tr] = true</c> (<c>:102-111</c>).
        ///
        /// <para><b>Idempotent, and that is load-bearing.</b> It runs from every commander's every tick
        /// until it succeeds, and from the same commander again after a room rebuild. Marking itself
        /// live twice is harmless; creating a second <c>Coord</c> for the same room would split the
        /// firing right in two.</para>
        /// </summary>
        protected virtual bool EnsureCoord()
        {
            if (_coord != null) return true;

            RoomInstance room = CurrentRoom;
            if (room == null) return false;

            _coord = room.bossCoord ?? (room.bossCoord = new BossEnclCoord(RollInclusive));
            _coord.SetAlive(_commanderIndex, true);
            return true;
        }

        /// <summary>The room this commander is standing in, from whichever seam has it.</summary>
        protected RoomInstance CurrentRoom
        {
            get
            {
                if (_tileQuery != null && _tileQuery.Room != null) return _tileQuery.Room;
                if (_controller != null) return _controller.Room;
                return null;
            }
        }

        /// <summary>
        /// <c>Math.random()</c> for the coord's two draws, injected so the rotation can be pinned
        /// offline. <c>max</c> is inclusive, which is what <c>floor(random * n)</c> produces.
        /// </summary>
        static int RollInclusive(int max) => UnityEngine.Random.Range(0, max + 1);

        /// <summary>
        /// <c>control()</c>'s body in the oracle's order: stun latch, the timer, the target poll, facing,
        /// flight, then <c>attack()</c> (<c>:218-298</c>).
        /// </summary>
        protected virtual void TickControl(int tickIndex)
        {
            // `if(stun) { aiState = 0; aiTCh = 3; walk = 0; }` (:218-223). The stun restarts the hover
            // counter, so a shocked commander freezes its drift for three ticks and then resumes from
            // the same phase.
            if (_blackboard.ShockTimerTicks > 0)
            {
                _aiState = 0;
                _aiTicks = BossEnclRules.StunTicks;
            }

            // `if(aiTCh > 0) --aiTCh; else { aiState = 1; aiTCh = floor(random*60+150); }` (:236-244).
            if (_aiTicks > 0)
            {
                _aiTicks--;
            }
            else
            {
                _aiState = 1;
                _aiTicks = BossEnclRules.AiTicksFor(
                    UnityEngine.Random.Range(0, BossEnclRules.AiTicksRollRange));
            }

            // `if(aiTCh % 40 == 1) { if(pet && ...) setCel(pet); else setCel(gg); }` (:245-255).
            //
            // The pet arm is not ported: the port has no companion unit to prefer, and AS3 only reaches
            // it when the player owns one. `setCel(gg)` is the remaining branch — re-seat the target
            // from perception, which has already filtered for liveness and faction.
            //
            // <b>This poll is the ONLY thing that ever sets `celUnit`, and that makes the commander
            // passive for its first several seconds.</b> `UnitBossEncl` never calls `findCel()`
            // (grep: the class has no call site), and `setPos` explicitly nulls the target at spawn
            // (`Unit.as:1879`). So `attack()`'s `if(celUnit)` refuses until this line first fires, which
            // — starting from `aiTCh = 30`, expiring to 150..209, and firing at `% 40 == 1` — lands
            // roughly 150..208 ticks later. Only two of the sixty rolls (161 and 201) poll immediately.
            // A play-tester will report "the boss does not shoot at first"; this is why, and it is the
            // oracle's behaviour rather than a missing wire.
            if (BossEnclRules.IsCelPollTick(_aiTicks))
            {
                _celUnit = _blackboard.TargetUnit;
            }

            // `storona = celDX > 0 ? 1 : -1;` (:256) — unconditional, and `celDX` is 0 with no target,
            // so an unaware commander faces left. That is the oracle's behaviour, not a fallback.
            ApplyFacing(_blackboard.TargetDeltaX > 0f ? 1 : -1);

            if (_aiState != 0)
            {
                StepFlight();
            }

            if (_aiState == 1)
            {
                Attack();
            }
        }

        /// <summary>
        /// The flight block (<c>:258-294</c>): convert the body to AS3 room-local pixels, let the rules
        /// integrate one frame, and hand the two speeds to the movement layer.
        ///
        /// <para><b>Position is read back from the transform every tick, velocity is not.</b> The motor
        /// owns integration, so <c>X</c>/<c>Y</c> are whatever the body actually reached — which is what
        /// the oracle's wall tests read. <c>dx</c>/<c>dy</c> are this brain's own accumulation, exactly
        /// as in AS3, and must persist across ticks or the commander would restart from zero every
        /// frame and never build up speed.</para>
        /// </summary>
        protected virtual void StepFlight()
        {
            if (!TryGetRoomFrame(out Vector2 originPixel, out int roomHeightTiles)) return;

            Vector2 as3 = WorldCoordinates.UnityWorldToAs3RoomLocal(
                transform.position, originPixel, roomHeightTiles);

            _flight.X = as3.x;
            _flight.Y = as3.y;

            BossEnclRules.StepFlight(
                ref _flight,
                active: true,
                maxSpeed: MaxFlightSpeed,
                accel: FlightAcceleration);

            MoveHorizontal(_flight.Dx);

            // The single axis mirror in the whole model — see `PortVerticalSpeedFromAs3`.
            SetVerticalSpeed(BossEnclRules.PortVerticalSpeedFromAs3(_flight.Dy));
        }

        /// <summary>
        /// The room origin and height the AS3↔port conversion needs, or <c>false</c> when this commander
        /// has no room yet (the first frames of a spawn).
        /// </summary>
        protected bool TryGetRoomFrame(out Vector2 originPixel, out int roomHeightTiles)
        {
            originPixel = Vector2.zero;
            roomHeightTiles = 0;

            RoomInstance room = CurrentRoom;
            if (room == null || _tileQuery == null) return false;

            originPixel = _tileQuery.OriginPixel;
            roomHeightTiles = room.height;
            return roomHeightTiles > 0;
        }

        /// <summary>AS3 <c>maxSpeed</c> — the row's <c>speed</c>, i.e. <c>moveSpeed</c> (15 for <c>bossencl</c>).</summary>
        protected float MaxFlightSpeed
        {
            get
            {
                UnitDefinition def = _controller?.Definition;
                return def != null && def.moveSpeed > 0f ? def.moveSpeed : 15f;
            }
        }

        /// <summary>AS3 <c>accel</c> — the row's <c>acceleration</c> (1.5 for <c>bossencl</c>).</summary>
        protected float FlightAcceleration
        {
            get
            {
                UnitDefinition def = _controller?.Definition;
                return def != null && def.acceleration > 0f ? def.acceleration : 1.5f;
            }
        }

        /// <summary>
        /// <c>attack()</c> (<c>:301-311</c>):
        /// <code>
        /// if(celUnit) {
        ///    attKorp(celUnit);
        ///    if(this.coord.tr == this.tr &amp;&amp; this.coord.t1 &gt; 45) currentWeapon.attack();
        /// }
        /// </code>
        ///
        /// <para><b>The contact hit is ungated and the shot is gated.</b> All three commanders bump the
        /// player when they touch it; only the one holding the firing right shoots. That asymmetry is
        /// the fight: the other two are still dangerous to stand next to, which is what stops the arena
        /// from being "run at the one that is quiet".</para>
        /// </summary>
        protected virtual void Attack()
        {
            if (_celUnit == null) return;

            _controller.TryContactAttack(_celUnit, 1f);          // `attKorp(celUnit)`

            if (_coord != null && _coord.CanFire(_commanderIndex))
            {
                EnclController?.FireCommanderWeapon();           // `currentWeapon.attack()`
            }
        }

        /// <summary>
        /// The base ladder's attack hook, which this brain never reaches.
        ///
        /// <para><b>It forwards to <see cref="Attack"/> rather than throwing or returning, and that is
        /// deliberate.</b> <see cref="EnemyBrain.ExecuteAttackAction"/> is abstract, and
        /// <see cref="SimTick"/> here replaces the ladder that would call it — so this is unreachable
        /// today. A stub that did nothing would be a silent trap the day something else drives this
        /// brain through the base tick; forwarding keeps one definition of "the commander attacks".
        /// </para>
        /// </summary>
        protected override void ExecuteAttackAction() => Attack();

        // ─────────────────────────────────────────────────────────────────────────────────────────
        //  Animation
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>animate()</c> (<c>:134-164</c>): <c>isFly ? "fly" : "stay"</c>, with the port's corpse
        /// convention layered on because the unit authors no <c>die</c> row
        /// (<c>AllData.as:645-646</c>).
        ///
        /// <para><b>The ids are spelled here as literals, and the branch is still the rule's.</b>
        /// <c>EnemyFamiliesAndBossesTests.Lint_EveryBrainResolveAnimState_ReturnsOnlyValidAs3IdsOrNull</c>
        /// reads every <c>*Brain.cs</c> for <c>return "&lt;id&gt;"</c> and checks it against
        /// <c>AnimationSet.As3Ids</c>; a bare delegation to <c>BossEnclRules.ResolveAnimState</c> would be
        /// invisible to that scan, and an invisible return is exactly the case the lint exists to catch.
        /// So the rule decides and the literals are what this method hands back — the two cannot drift
        /// silently, because the rules fixture pins the rule's own two answers by name.</para>
        /// </summary>
        protected override string ResolveAnimState()
        {
            if (_controller == null) return null;

            // `isFly` is true for this unit's whole life (:92), so the rule is evaluated at `true`.
            string state = BossEnclRules.ResolveAnimState(_controller.IsAlive, isFly: true);
            return state == BossEnclRules.FlyAnimId ? "fly" : "stay";
        }
    }
}
