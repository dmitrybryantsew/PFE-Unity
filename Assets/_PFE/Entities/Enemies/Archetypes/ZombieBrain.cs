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
    /// <para><b>What is still deliberately not modelled, and why.</b> <c>UnitZombie</c> is the largest
    /// unit script in the oracle — 1239 lines — and the remainder is the <b>super</b> system
    /// (<c>setSuper</c>/<c>findSuper</c>/<c>superSila</c>/<c>superSila2</c>/<c>superSilaVse</c>,
    /// <c>:954-1228</c>), <b>resurrection</b> (<c>t_res</c>/<c>resurrect</c>, <c>:495-525</c>), the
    /// <b>quake</b> and the two venom weapons. None of it is reachable without the data that drives it:
    /// <c>superSilaTip</c> comes from <c>&lt;un ss=…&gt;</c> and <c>isRes</c> from <c>&lt;un res/&gt;</c>
    /// — and the port's <c>UnitDefinition</c> has a reader for <c>res</c>
    /// (<see cref="UnitDefinition.canResurrect"/>) but <b>not</b> for <c>ss</c>. So the variants that
    /// carry those attributes (zombie2, zombie5, ...) will behave as plain ghouls until that workstream
    /// lands — except that <c>canResurrect</c> is now read by nothing but the importer, so a resurrecting
    /// variant still looks identical to a plain one.</para>
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

            // vykop() also does `aiSpok = maxSpok + 10` (:453) — it wakes up already committed to the
            // hunt rather than drifting back into patrol. The port has no maxSpok, so the nearest honest
            // stand-in is the existing alert duration; recorded as an approximation rather than presented
            // as the oracle's number.
            _blackboard.AlertTimerTicks = _patrolDurationTicks;

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
                _sensors.RaiseAlarm(_blackboard, damageSourcePx, durationTicks: 90);
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
            return Mathf.Abs(_blackboard.TargetDeltaX) < OptDistAttackPixels
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

            attacker.TryContactAttack(target, scale);
        }

        /// <summary>
        /// AS3 <c>UnitZombie.animate()</c> (<c>UnitZombie.as:215-320</c>) — the animation state the
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
                // AS3 `sost` is 1 alive, 2 dead, 3 dead-with-resurrection-pending. The port has no
                // `t_res` countdown, so `sost == 3` is unreachable and both dead states take the block
                // below. `UnitDefinition.canResurrect` now carries the data (`res='1'` on zombie7/8/9)
                // but still has no reader — UnitZombie.as:218-232, the countdown that draws `die` for
                // its last 20 ticks, is not modelled. That is why a resurrecting variant currently
                // looks identical to a plain one.
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

            // AS3 aiState 4 and 7 are `pre` (super wind-up) and `super` (UnitZombie.as:256-264). Neither
            // is reachable from this slice — both need `setSuper()` and the `<un ss=…>` data — and
            // selecting one anyway would be worse than not selecting it: `zombie0` owns neither row, so
            // the animator would fall through to the state already showing and the mistake would be
            // invisible. That is also why `EnemyAIState.Attack` is never mapped to `super` — the port's
            // `Attack` is a generic swing, AS3's 7 is the super, and the two are not the same thing.
            if (!_controller.IsGrounded)
            {
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
