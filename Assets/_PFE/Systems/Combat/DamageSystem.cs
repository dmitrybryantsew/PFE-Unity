using System.Collections.Generic;
using UnityEngine;
using VContainer.Unity;
using MessagePipe;
using PFE.Core;
using PFE.Core.Messages;
using PFE.Core.Rng;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Weapons;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// The one place final damage numbers are computed, and the one place they are applied.
    ///
    /// <para><b>Shape.</b> A damage source <i>reports</i> a hit (<see cref="Report"/>); this system
    /// resolves it against the target's armour at <see cref="SimTickOrder.Damage"/> — after every
    /// source has finished moving for the tick. It replaces the static
    /// <c>DamageResolver</c>, which had three faults worth recording: it hardcoded
    /// <c>armour = 0f</c> so armour never applied, it treated <c>Piercing</c> as a 0..1 fraction
    /// (<c>armour * (1 - clamp01(piercing))</c>) where AS3 subtracts it flatly, and it owned a static
    /// mutable fallback RNG (<c>s_fallbackCombatRng</c>) that is exactly the hidden global singleton a
    /// lockstep peer cannot reproduce.</para>
    ///
    /// <para><b>It also owns whether a hit happens at all, and how hard.</b> Before any damage term runs,
    /// <see cref="HitAvoidance.RollsHit"/> ports AS3's four-term <c>udarBullet</c> conjunction —
    /// the attacker's under-skill <c>miss</c>, then the target's evasion, which is
    /// <i>accuracy vs dexterity</i> for a projectile and a <i>dodge</i> probability for a melee swing.
    /// A miss resolves to nothing (no wear, no crit) and is published as
    /// <c>DamageDealtMessage.isMiss</c>. A hit that lands is then spread by
    /// <see cref="DamageVariance"/>, AS3's <c>× (0.7 .. 1.3)</c> (<c>Unit.as:4085</c>). Blasts skip both,
    /// as they do in AS3.</para>
    ///
    /// <para><b>Timing is a flag; the decision is not.</b> <c>PfeDebugSettings.SimTickDamage</c>
    /// chooses <i>when</i> a reported hit is <i>applied</i> — at the drain, or immediately at report
    /// time. It does not choose the formula, and it does not choose when the hit is <i>decided</i>:
    /// the <see cref="HitAvoidance"/> roll is taken at report time in both modes, because its answer is
    /// the bit a projectile needs to know whether to keep flying. Deferring only the application is
    /// what makes tick mode safe for a projectile; deferring the decision is what made an evaded round
    /// stop dead. The two modes are therefore <b>not</b> numerically identical — they draw from
    /// different streams, so a tick-mode burst and an immediate-mode burst assign different numbers to
    /// the same rolls. That is a property of batching, not a defect, and it is why the flag is a
    /// determinism switch rather than a free toggle.</para>
    ///
    /// <para><b>One formula term is separately switchable, and that one does change the numbers.</b>
    /// <c>PfeDebugSettings.ApplyVulnerabilities</c> gates the target's vulnerability multiply (AS3
    /// <c>Unit.damage():3527-3530</c>) and defaults <b>off</b>. It is not a timing choice like the flag
    /// above: switching it on applies AS3's baseline, which carries <c>emp = 0</c>, so every unit becomes
    /// EMP-immune until a <c>&lt;vulner&gt;</c> element grants it back (25 of 94 do). That is a real
    /// gameplay change, so it follows step 4's precedent — land behind a flag, default off, play-test
    /// before it becomes the default.</para>
    ///
    /// <para><b>Never queues when nothing will drain it.</b> If the sim is not dispatching
    /// (<c>SimTickEnabled</c> off) or this system never made it onto <see cref="SimLoop"/>, a queued hit
    /// would never resolve — so <see cref="Report"/> resolves immediately instead. A queue that only
    /// exists when a drainer exists is the difference between "damage lands a tick late" and "damage
    /// silently stops working".</para>
    ///
    /// <para><b>RNG.</b> In tick mode the stream is salted with the tick index
    /// (<c>GetStream(RngStream.Combat, tickIndex)</c>), so a tick's rolls are a pure function of the
    /// tick and its inputs. It is created <b>once per tick</b> and shared by every roll in it, because
    /// <c>PcgRngService.GetStream(stream, salt)</c> constructs a fresh generator per call — fetching
    /// per roll would hand every roll in the tick an identical stream, and therefore identical crit
    /// rolls. Since the avoidance roll now runs at report time and the damage rolls at the drain, that
    /// one generator is memoized across both halves of the tick rather than fetched at the drain. See
    /// <see cref="TickRng"/>. In immediate mode there is no tick index to salt with, so the persistent
    /// <c>Combat</c> child stream is used.</para>
    /// </summary>
    public sealed class DamageSystem : IStartable, ISimTickable
    {
        private readonly IDamageCalculator _calculator;
        private readonly IRngService _rngService;
        private readonly IPublisher<DamageDealtMessage> _publisher;
        private readonly PfeDebugSettings _debugSettings;
        private readonly SimLoop _simLoop;

        /// <summary>Hits awaiting the next <see cref="SimTick"/>.</summary>
        private List<PendingDamage> _pending = new();

        /// <summary>Hits being drained. Swapped with <see cref="_pending"/> so reports made during a
        /// drain (a death that spawns something) cannot mutate the list under iteration.</summary>
        private List<PendingDamage> _draining = new();

        private IRngService _immediateRng;
        private bool _registeredOnSimLoop;

        /// <summary>
        /// The combat stream for the tick currently being built, memoized so that the report-time
        /// avoidance rolls and the drain-time damage rolls of one tick draw from a single sequence.
        /// See <see cref="TickRng"/>.
        /// </summary>
        private IRngService _tickRng;
        private int _tickRngIndex = int.MinValue;

        public DamageSystem(
            IDamageCalculator calculator,
            IRngService rngService,
            IPublisher<DamageDealtMessage> publisher,
            PfeDebugSettings debugSettings,
            SimLoop simLoop)
        {
            _calculator = calculator;
            _rngService = rngService;
            _publisher = publisher;
            _debugSettings = debugSettings;
            _simLoop = simLoop;
        }

        /// <summary>
        /// After every damage source has moved. A hit is resolved against the position the tick left
        /// behind, not the position the source had when it reported.
        /// </summary>
        public int TickOrder => SimTickOrder.Damage;

        /// <summary>Hits queued and not yet drained. Always 0 in immediate mode.</summary>
        public int PendingCount => _pending.Count;

        /// <summary>Hits whose outcome was applied. Excludes hits skipped because the target was gone.</summary>
        public int ResolvedCount { get; private set; }

        /// <summary>
        /// Hits dropped because the target died or was destroyed before the drain. Not an error — a
        /// bullet that lands on a corpse and one that lands on an already-despawned unit are both
        /// normal — but a number that should be small, so it is worth being able to see.
        /// </summary>
        public int SkippedCount { get; private set; }

        /// <summary>
        /// Hits that reached a live target and were <b>evaded</b> — the
        /// <see cref="HitAvoidance.RollsHit"/> conjunction returned false.
        ///
        /// <para>Distinct from <see cref="SkippedCount"/>, which counts hits that found nothing to land
        /// on. This one counts hits that landed on something and were dodged, so it is the observable
        /// that proves the evasion path is live rather than inert: if it stays 0 while shooting at a
        /// high-<c>dexter</c> target, the roll is not running.</para>
        /// </summary>
        public int MissedCount { get; private set; }

        /// <summary>
        /// True when a reported hit will actually be drained: the flag is on <b>and</b> this system is
        /// on the loop <b>and</b> the loop is dispatching. All three, or <see cref="Report"/> resolves
        /// inline.
        /// </summary>
        public bool IsTickAligned =>
            _registeredOnSimLoop
            && _debugSettings != null
            && _debugSettings.SimTickEnabled
            && _debugSettings.SimTickDamage;

        public void Start()
        {
            if (_simLoop == null)
            {
                Debug.LogWarning(
                    "[DamageSystem] No SimLoop; damage will resolve immediately at report time.");
                return;
            }

            _simLoop.Register(this);
            _registeredOnSimLoop = true;
        }

        /// <summary>
        /// Records a hit and answers what became of it.
        ///
        /// <para><b>The avoidance decision is made here, in both modes; only the application is ever
        /// deferred.</b> In immediate mode the two are the same instant, which is AS3's shape. In tick
        /// mode the damage terms wait for the drain but the <see cref="HitAvoidance"/> roll does not —
        /// because its answer is not only a damage input, it is the bit that tells a projectile whether
        /// to keep flying (AS3 <c>weapon/Bullet.as:535</c>). A round that only learned it had missed a
        /// tick after the contact would have travelled up to a full tick past the target by then, and
        /// straight through whatever stood behind it.</para>
        ///
        /// <para><b>The caller does not learn the damage</b> — a source reports, it does not read back a
        /// number. The verdict is not the damage. It is the single bit a projectile needs to decide
        /// whether to stop, and it is produced <i>here</i> rather than by the caller precisely so that
        /// the avoidance roll is not duplicated: AS3's bullet reads that bit straight off
        /// <c>udarBullet</c>'s return (<c>weapon/Bullet.as:535</c>), and the port moved the roll into
        /// this system, so this is where the bit has to come back out. See <see cref="DamageVerdict"/>.</para>
        /// </summary>
        public DamageVerdict Report(in PendingDamage hit)
        {
            IDamageable target = hit.Target;

            // A null target is a caller fault, not a skipped hit — see SkippedCount.
            if (target == null)
                return DamageVerdict.Ignored;

            // Liveness is checked here as well as at the drain, and the two checks exist for different
            // reasons. This one keeps a roll from being spent on a target that is already gone: the
            // oracle tests `sost == 4 || disabled` before it calls `udarBullet` at all
            // (`weapon/Bullet.as:504`), so a corpse consumes no random number. The drain-time check
            // catches a target that an earlier hit in the same drain killed.
            if (IsGone(target) || !target.IsAlive)
            {
                SkippedCount++;
                return DamageVerdict.Ignored;
            }

            bool tickAligned = IsTickAligned;

            // The stream has to be picked BEFORE the roll, and the tick index is only knowable from the
            // loop — a report arrives from inside the tick being dispatched, so `TickIndex` is already
            // this tick's. In immediate mode there is no tick to salt with.
            IRngService rng = tickAligned ? TickRng(_simLoop.TickIndex) : ImmediateRng();

            if (!RollsHit(hit, rng))
                return DamageVerdict.Evaded;

            if (tickAligned)
            {
                _pending.Add(hit);
                return DamageVerdict.Queued;
            }

            return Apply(hit, rng);
        }

        public void SimTick(int tickIndex)
        {
            if (_pending.Count == 0)
                return;

            // Swap first: anything reported during the drain below must land in the fresh list.
            (_draining, _pending) = (_pending, _draining);
            _pending.Clear();

            // The same generator this tick's reports already drew their avoidance rolls from. Fetching
            // a fresh one here would restart the sequence, handing the armour and crit rolls the same
            // numbers the avoidance rolls just used. See TickRng.
            IRngService rng = TickRng(tickIndex);

            int count = _draining.Count;
            for (int i = 0; i < count; i++)
            {
                Apply(_draining[i], rng);
            }

            _draining.Clear();
        }

        // ── Resolution ───────────────────────────────────────────────────────

        /// <summary>
        /// The hit-avoidance decision, and the miss report that goes with a failed one. Returns
        /// <c>true</c> when the hit lands.
        /// </summary>
        /// <remarks>
        /// Two exclusions, both the oracle's:
        /// <list type="bullet">
        ///   <item><description>anything that reaches <c>Unit.damage()</c> without a bullet never rolls
        ///     — a blast (<c>Bullet.explRun</c> calls <c>unit.damage()</c> directly) and a prop impact
        ///     (<c>Unit.udarBox</c>, <c>Unit.as:4237</c>, likewise), so
        ///     <c>miss</c>/<c>precision</c>/<c>dodge</c> do not apply to either. That is
        ///     <see cref="PendingDamage.SkipsAvoidanceAndVariance"/>, which is a fact about the call
        ///     path and not about blasts.</description></item>
        ///   <item><description>the roll uses the same per-tick stream as the armour and crit rolls, in
        ///     the oracle's order. <see cref="HitAvoidance.RollsHit"/> reproduces AS3's short-circuits
        ///     exactly, so a shot with <c>miss = 0</c> against a non-evading target consumes no roll
        ///     here and the crit stream stays where AS3 would leave it.</description></item>
        /// </list>
        /// </remarks>
        private bool RollsHit(in PendingDamage hit, IRngService rng)
        {
            if (hit.SkipsAvoidanceAndVariance
                || HitAvoidance.RollsHit(hit.Context, hit.Target.Evasion, hit.TravelDistancePixels, rng))
            {
                return true;
            }

            MissedCount++;

            // Published as a miss rather than silence: AS3 shows a "miss" number
            // (`Unit.as:4103-4117`, `txtMiss`), and this message is the only hook the presentation
            // layer has for it.
            _publisher?.Publish(new DamageDealtMessage
            {
                damage     = 0f,
                position   = hit.ImpactPosition,
                isCritical = false,
                isMiss     = true,
            });

            // The floating-damage overlay's feed. Written unconditionally and gated at the view, so
            // the overlay can be switched on mid-burst without having missed the hits that led up to
            // it — a diagnostic that only records from the moment you enable it cannot show you the
            // shot you enabled it to catch.
            DamageEventFeed.Default.Report(
                hit.ImpactPosition, amount: 0f, isCritical: false, isMiss: true);

            // AS3 returns -1 here, and that -1 is what keeps the round alive: `Bullet.run` only
            // calls `popadalo()` (the sole setter of the stop flag) when the return is >= 0.
            return false;
        }

        /// <summary>
        /// Applies a hit whose avoidance decision has already been made, and reports what became of it.
        /// The verdict is the <b>only</b> thing a caller gets back — not the damage, which stays here.
        /// </summary>
        private DamageVerdict Apply(in PendingDamage hit, IRngService rng)
        {
            IDamageable target = hit.Target;

            // Re-checked at drain time, not just at report time: an earlier hit in this same drain may
            // have killed it, and the GameObject may be gone entirely.
            if (IsGone(target) || !target.IsAlive)
            {
                SkippedCount++;
                return DamageVerdict.Ignored;
            }

            DamageContext ctx = hit.Context;

            float incoming = hit.IsExplosion
                ? ExplosionDamageFor(ctx, hit)
                : ctx.BaseDamage;

            // AS3 `if(param1.damage > 0)` wraps the whole hit block and falls through to `return 0` when
            // it does not hold — and 0 is >= 0, so the round still stops. A bullet that carries no
            // damage is absorbed, not passed through.
            if (incoming <= 0f)
                return DamageVerdict.Landed;

            // ── Damage spread ───────────────────────────────────────────────────────────────────
            // AS3 `Unit.as:4085` — `_loc4_ = param1.damage * (Math.random() * 0.6 + 0.7)`, inside the
            // `if (param1.damage > 0)` block, so it sits AFTER that gate (hence below, not above) and
            // BEFORE `this.damage()`, whose body applies vulnerability then armour then crit. So the
            // spread lands on the pre-armour number, and crit amplifies the spread value — the
            // oracle's order.
            //
            // Blasts and contact hits are excluded: both reach `unit.damage()` without passing through
            // `udarBullet`, which is where `:4085`'s spread lives — so an explosion does its falloff
            // damage exactly and a crate impact does its `vel2` damage exactly. See
            // PendingDamage.SkipsAvoidanceAndVariance; this used to test IsExplosion, which read as a
            // fact about blasts when the real rule is a fact about the call path.
            //
            // The draw happens even under the testDam debug toggle — `DamageVariance.Roll` takes the
            // roll first and only then decides whether to report it — because the combat stream is
            // shared with the armour-reliability and crit rolls below. Skipping the draw would shift
            // every later roll in the same tick, which is a replication bug, not a debug convenience.
            if (!hit.SkipsAvoidanceAndVariance)
            {
                incoming *= DamageVariance.Roll(rng, _debugSettings != null && _debugSettings.TestDamage);
            }

            // ── Vulnerability ───────────────────────────────────────────────────────────────────
            // AS3 `Unit.damage():3527-3530` — `if(param2 < kolVulners) param1 *= this.vulner[param2]`.
            // It is the FIRST term applied, and that ordering is load-bearing: the armour pool block
            // reads param1 at `:3580` (`_loc9_ = param1`), so the wear is computed from the
            // post-vulnerability damage, not the raw hit. Applying this after the wear would give a
            // resistant target armour that lasts longer than the oracle's.
            incoming *= VulnerabilitiesFor(target).GetVulnerability(ctx.DamageType);

            // AS3 `:3563-3566` — `if(param1 == 0) return 0`. A zeroed hit is not "0 damage applied":
            // it does nothing at all. No wear, no floating number, no event — so the resolver must not
            // fall through and hand `DamageOutcome.None` to a target that would still record a hit.
            //
            // It still STOPS the round, though: `Unit.damage()` returns 0 and `udarBullet` returns that
            // 0, which is >= 0. Being immune to a damage type is not the same as being transparent to it.
            if (incoming <= 0f)
            {
                // ...and it is still THROWN. `udarBullet` calls `otbros` after `damage()` whatever
                // `damage()` returned (`Unit.as:4090-4091`), so a target immune to the type is pushed
                // anyway. The draw order is unaffected: this path never reached the armour or crit rolls,
                // so the knockback draw is the next one either way.
                ApplyKnockback(hit, target, ctx, rng);
                return DamageVerdict.Landed;
            }

            ArmourState armour = target.Armour;

            DamageOutcome outcome = _calculator.ResolveDamage(
                incomingDamage: incoming,
                armourIntegrityDamage: armour.WearFrom(
                    ctx.DamageType, incoming, armourMultiplier: ctx.ArmorMultiplier),
                armour: armour,
                rng: rng,
                damageType: ctx.DamageType,
                piercing: ctx.Piercing,
                armourMultiplier: ctx.ArmorMultiplier,
                critChance: ctx.CritChance,
                critMultiplier: ctx.CritMultiplier,
                // AS3 `Unit.damage():3611-3632` — the target's natural resistance, assigned as the
                // floor of whichever reduction branch the damage type reaches, before the
                // probabilistic armour roll adds to it. Read from the target, because it is not a
                // constant: UnitTrain raises it to 20 for the armoured variant and Unit.setLevel()
                // scales it by level (:1611). This used to be a literal 0f — the calculator applied
                // a term no caller could ever supply, so the armoured training dummy resolved
                // identically to the plain one. A target with no stats answers 0, which is AS3's
                // default and leaves every unarmoured unit's numbers unchanged.
                skinResistance: target.SkinResistance,
                // ── The two attacker-side crit channels, and the target-side gate ────────────────
                // AS3 `Unit.damage():3659-3666` (stealth crit) and `:3671-3677` (disintegration).
                // Both values ride on the shot — the context carried them from fire time — while the
                // two target reads are live: `this.doop` and `this.hp`.
                //
                // `target.CurrentHealth` is the health BEFORE this hit, which is what the oracle's
                // `this.hp` holds at :3671: the damage() body has not yet subtracted `param1` (that
                // happens at :3705). Passing the post-hit value would make the disintegr gate test the
                // wrong number.
                critInvisChance: ctx.CritInvis,
                desintegrChance: ctx.Desintegr,
                targetCurrentHp: target.CurrentHealth,
                targetIsNonLiving: target.IsNonLiving);

            target.ApplyDamage(outcome);
            ResolvedCount++;

            // ── On-hit status effects — AS3 `Unit.damage():3763-3834` ────────────────────────────
            //
            // The oracle runs this block inside damage(), AFTER the hp subtraction (`:3705`) and BEFORE
            // the `otbros` throw (`:4090-4091`). So it belongs here — after ApplyDamage, before
            // ApplyKnockback — and the three producers run in source order.
            //
            // Two of the three need a roll, and the oracle's order is fixed: the contusion draw comes
            // first (`:3763`), then the dopEffect draw (`:3771`), then the unconditional ammoFire
            // (`:3830`). Taking them out of order would shift every later draw in the tick, which is a
            // replication divergence rather than a gameplay bug — see the RNG notes on this class.
            //
            // Short-circuiting matters as much as order: `dopCh >= 1` (62 of the 80 data rows) spends
            // NO draw, and a contusion roll happens only for an explosive hit on a living, non-robot
            // target. Both are expressed by only drawing when the guard passes.
            ApplyOnHitEffects(hit, target, rng);

            _publisher?.Publish(new DamageDealtMessage
            {
                damage = outcome.HpDamage,
                position = hit.ImpactPosition,
                isCritical = outcome.IsCritical,
                // Reached only past the avoidance roll above, so a published hit genuinely landed.
                isMiss = false,
            });

            // See the miss path above for why this is unconditional rather than gated on the channel.
            DamageEventFeed.Default.Report(
                hit.ImpactPosition, outcome.HpDamage, outcome.IsCritical, isMiss: false);

            // Last, and the position is load-bearing: AS3 runs `otbros()` after `this.damage()`
            // (`Unit.as:4090-4091`), so this draw has to follow the armour and crit draws. Taking it any
            // earlier would hand every later hit in the tick a stream one draw out of step.
            ApplyKnockback(hit, target, ctx, rng);

            return DamageVerdict.Landed;
        }

        /// <summary>
        /// Applies AS3's <c>Unit.otbros()</c> — the throw a landed hit gives its target.
        /// </summary>
        /// <remarks>
        /// <para><b>Direct hits only, and the predicate is the call path.</b> <c>otbros</c> has exactly
        /// <b>one</b> call site in the oracle — <c>Unit.as:4091</c>, inside <c>udarBullet</c>, one line
        /// after <c>this.damage()</c> at <c>:4090</c>. So "this hit did not go through
        /// <c>udarBullet</c>" is also the predicate for "this hit gets no throw", which is exactly
        /// <see cref="PendingDamage.SkipsAvoidanceAndVariance"/>. That is why the guard below reads the
        /// flag rather than <c>IsExplosion</c>: a blast damages through <c>Bullet.explRun</c>, which calls
        /// <c>unit.damage()</c> directly, and a prop impact through <c>Unit.udarBox</c>
        /// (<c>:4237</c>), which does the same — neither reaches <c>:4091</c>. (AS3's blasts <i>do</i>
        /// push, but by a separate mechanism: <c>explBlast</c> spawns a radial child bullet per unit,
        /// aimed away from the centre with <c>knockx = dx / vel</c>. The port does not model that; it is a
        /// missing feature, and inventing blast knockback here would be a different wrong answer.)</para>
        ///
        /// <para><b>Why the <c>IsExplosion</c> form was wrong even though it looked right.</b> It produced
        /// the correct answer for the one non-<c>udarBullet</c> caller that existed when it was written,
        /// and it kept producing it for blasts after <see cref="PendingDamage.Contact"/> was added — but a
        /// crate impact fell through to the draw below and consumed a knockback roll the oracle never
        /// takes. The impulse it then produced was zero (<c>DamageContext.Contact</c> stamps no knock and
        /// no direction), so nothing visible would have exposed it: a silent extra draw on the shared
        /// combat stream is a replication divergence, not a gameplay bug, which is the kind that only
        /// shows up as "the same seed gives different numbers on the second peer".</para>
        ///
        /// <para><b>The draw happens even for a zero-knock weapon.</b> AS3 draws before multiplying by
        /// <c>otbros</c>, so short-circuiting on <c>Knockback == 0</c> — which most weapons carry — would
        /// skip a draw the oracle takes and desynchronise every later roll in the tick. Only
        /// <see cref="IDamageable.IsInvulnerable"/> skips the draw, because AS3 returns before it in that
        /// one case.</para>
        ///
        /// <para><b>The impulse is converted here, and this is the only place that may do it.</b>
        /// <see cref="DamageContext.Knockback"/> is AS3's <c>otbros</c> — a per-<i>frame</i> velocity in
        /// pixels, because <c>Unit.otbros()</c> adds it straight to <c>dx</c>/<c>dy</c>
        /// (<c>Unit.as:4254-4255</c>), which <c>Unit.run()</c> then adds to <c>X</c> once per frame. The
        /// port's <see cref="IDamageable.ApplyKnockback"/> feeds <c>UnitController._velocity</c>, which is
        /// Unity units per <b>second</b> — it is multiplied by <c>Time.fixedDeltaTime</c> in
        /// <c>UnitController.Move</c>. Those are different quantities, and
        /// <see cref="TileQueryConstants.PerFrameVelocityToUnitsPerSecond"/> is the conversion. Omitting
        /// it made every shot throw 1/0.3 = <b>3.33×</b> too hard, which is exactly what the minigun
        /// play-test reported; for a high-<c>knock</c> weapon it is the difference between a stagger and
        /// a launch. <see cref="KnockbackMath"/> stays unit-free on purpose — it models the oracle's
        /// <c>random * 0.4 + 0.8</c> and the <c>knocked / massa</c> ratio, and knows nothing about frames
        /// or seconds.</para>
        /// </remarks>
        private static void ApplyKnockback(in PendingDamage hit, IDamageable target,
                                           in DamageContext ctx, IRngService rng)
        {
            // The oracle's single `otbros` call site is inside `udarBullet` (`Unit.as:4091`), so the
            // flag that means "reached `damage()` without `udarBullet`" is the one that means "no
            // throw" — see the remarks. Returning on `IsExplosion` instead would let a crate impact
            // (`udarBox`) take this draw.
            if (hit.SkipsAvoidanceAndVariance) return;

            // AS3 `Unit.otbros():4245-4248` returns BEFORE the draw, so this test has to come first.
            if (target.IsInvulnerable) return;

            float scale = KnockbackMath.Roll(rng, target.Knocked, target.Mass);

            // px/frame → units/s, at the boundary where the oracle's units stop and Unity's begin.
            float speedUnitsPerSecond = ctx.Knockback * scale
                                      * TileQueryConstants.PerFrameVelocityToUnitsPerSecond;

            Vector2 impulse = ctx.KnockbackDir * speedUnitsPerSecond;

            // The draw above is unconditional — see the remarks. Only the WRITE is skippable, and it is
            // skipped when it would be a no-op anyway: most weapons carry `@knock = 0`, and a context
            // whose direction was never stamped (DamageContext.FromWeapon's zero) means "no knockback",
            // deliberately, rather than an invented one.
            if (impulse.sqrMagnitude <= 0f) return;

            target.ApplyKnockback(impulse);
        }

        /// <summary>
        /// Runs AS3's on-hit status-effect producers against a landed hit, taking the two rolls the
        /// oracle takes and in the oracle's order.
        /// </summary>
        /// <remarks>
        /// <para><b>The rolls belong here and not in <see cref="OnHitEffectProducers"/>.</b> AS3's
        /// producers are a sequence of <c>Math.random()</c> calls interleaved with the applications,
        /// on the same stream as the armour, crit and knockback rolls. A producer that rolled its own
        /// would take its draw at a different point in the sequence — which is exactly the reason
        /// <c>PendingDamage</c> threads the avoidance roll through instead of rolling internally.</para>
        ///
        /// <para><b>The draws are taken only when the guard passes.</b> <c>dopCh &gt;= 1</c> is the
        /// oracle's <c>(param3.weap.dopCh &gt; 0 &amp;&amp; (dopCh &gt;= 1 || Math.random() &lt; dopCh))</c>,
        /// so the usual case (ch='1') spends nothing, and the contusion roll is gated on an explosive
        /// hit on a living non-robot target. Rolling unconditionally would desynchronise the stream
        /// for every later hit in the tick while looking completely correct.</para>
        /// </remarks>
        private void ApplyOnHitEffects(in PendingDamage hit, IDamageable target, IRngService rng)
        {
            if (target is not IEffectReceiver receiver)
            {
                return;
            }

            DamageContext ctx = hit.Context;

            // The contusion draw — AS3 `:3763`. Gated on an explosive hit, the target being alive and
            // susceptible, and a draw against `param1 / maxhp`. A blast does reach this: `Bullet.explRun`
            // calls `unit.damage()` directly and so does take it, which is why the gate is on the
            // damage type rather than on the call path.
            bool contusionRolled = false;
            if (ctx.DamageType == DamageType.Explosive
                && !ContusionImmune(target)
                && target.IsAlive
                && MaxHealthOf(target) > 0f)
            {
                float chance = hit.IsExplosion
                    ? ExplosionDamageFor(ctx, hit) / MaxHealthOf(target)
                    : ctx.BaseDamage / MaxHealthOf(target);
                contusionRolled = rng.NextFloat() < chance;
            }

            // The dopEffect draw — AS3 `:3771`. `dopCh >= 1` is CERTAIN and spends no draw.
            bool dopChancePassed = ctx.DopChance >= 1f
                ? false   // ignored by the producer, which short-circuits the certain case
                : rng.NextFloat() < ctx.DopChance;

            OnHitEffectProducers.Apply(
                receiver,
                weaponDopEffect: ctx.DopEffect,
                weaponDopDamage: ctx.DopDamage,
                weaponDopChance: ctx.DopChance,
                dopChancePassed: dopChancePassed,
                ammoFireDamage: ctx.Ammo?.fireDamage ?? 0f,
                damageType: ctx.DamageType,
                isExplosiveHit: ctx.DamageType == DamageType.Explosive,
                wouldDie: contusionRolled,
                targetIsImmuneToContusion: ContusionImmune(target));
        }

        /// <summary>
        /// AS3's <c>!this.opt.robot &amp;&amp; !this.mech &amp;&amp; !this.doop</c> — a robot, mech or
        /// non-living target cannot be concussed. The port has only the <c>doop</c> flag on
        /// <see cref="IDamageable"/>; robot and mech are the same class-driven data gap as
        /// <c>isNonLiving</c>, so a machine currently reads as concussible. Recorded, not invented.
        /// </summary>
        private static bool ContusionImmune(IDamageable target) => target.IsNonLiving;

        private static float MaxHealthOf(IDamageable target) => target.MaxHealth;

        /// <summary>
        /// Blast damage for one target: linear falloff from the centre to the radius edge, scaled by the
        /// caller's per-target faction multiplier.
        /// </summary>
        private static float ExplosionDamageFor(in DamageContext ctx, in PendingDamage hit)
        {
            if (ctx.ExplosionDamage <= 0f)
                return 0f;

            float distance = Vector3.Distance(hit.ImpactPosition, hit.ExplosionCentre);
            float falloff = hit.ExplosionRadius > 0f
                ? Mathf.Clamp01(1f - distance / hit.ExplosionRadius)
                : 1f;

            return ctx.ExplosionDamage * falloff * hit.FactionMultiplier;
        }

        /// <summary>
        /// The target's vulnerability table, or the identity when the term is switched off.
        /// </summary>
        /// <remarks>
        /// <para><b>The two fallbacks differ on purpose, and choosing the wrong one is silent.</b> With
        /// the term <i>off</i> the answer is <see cref="VulnerabilityData.Unmodified"/> — all ones, which
        /// is what "this term does not exist" means, and it keeps the numbers byte-identical to the
        /// pre-A7 path. With the term <i>on</i> but no table on the target, the answer is
        /// <see cref="VulnerabilityData.Neutral"/> — AS3's baseline, which carries <c>emp = 0</c>.
        /// Substituting the identity for the neutral case would hand out EMP damage the oracle denies;
        /// substituting the neutral for the off case would make every unit EMP-immune the moment the
        /// flag was switched <i>off</i>, which is backwards.</para>
        /// </remarks>
        private VulnerabilityData VulnerabilitiesFor(IDamageable target)
        {
            if (_debugSettings == null || !_debugSettings.ApplyVulnerabilities)
                return VulnerabilityData.Unmodified;

            return target.Vulnerabilities;
        }

        /// <summary>
        /// Whether a target reference is unusable. <c>target == null</c> alone is not enough:
        /// <see cref="IDamageable"/> is an interface, so the compiler picks reference comparison, which
        /// does <b>not</b> see Unity's destroyed-object null. A destroyed MonoBehaviour would sail past
        /// a plain null check and then throw on first access.
        /// </summary>
        private static bool IsGone(IDamageable target)
        {
            if (target == null)
                return true;

            return target is Object unityObject && unityObject == null;
        }

        /// <summary>
        /// The persistent <c>Combat</c> child stream, for immediate mode. Created once and reused so
        /// successive hits draw from one sequence rather than restarting a generator each time.
        /// </summary>
        private IRngService ImmediateRng()
        {
            if (_rngService == null)
                return null;

            return _immediateRng ??= _rngService.GetStream(RngStream.Combat);
        }

        /// <summary>
        /// The combat stream for one tick: salted with the tick index, created on first use and reused
        /// by every later roll in the same tick — the report-time avoidance rolls and the drain-time
        /// damage rolls alike.
        /// </summary>
        /// <remarks>
        /// <para><b>Why it is memoized rather than fetched per call.</b>
        /// <c>PcgRngService.GetStream(stream, salt)</c> constructs a fresh generator on every call, so
        /// two calls with the same salt return two generators that emit <i>the same sequence</i>. That
        /// is harmless when there is one call per tick, which is what the drain used to do; it stops
        /// being harmless now that a report and the drain that follows it must continue one sequence —
        /// without the memo, the armour and crit rolls would be handed the very numbers the avoidance
        /// rolls had just used.</para>
        ///
        /// <para><b>Why the salt is the tick index and not a monotonic counter.</b> A tick's rolls have
        /// to be a pure function of the tick and its inputs. A counter would make them depend on how
        /// many rolls came before, which is exactly what a peer replaying from a snapshot cannot
        /// reproduce.</para>
        ///
        /// <para><b>The index comes from the loop.</b> <c>SimLoop.StepOnce</c> increments its index
        /// before it dispatches, so a report arriving from inside a tick — which is where every
        /// projectile reports from, at <c>SimTickOrder.Projectiles</c> — already sees that tick's index.
        /// A report that arrives from outside the loop entirely (a legacy physics callback) is
        /// attributed to the tick that last ran, which is deterministic but frame-paced; the probe path
        /// that replaced those callbacks reports from inside the tick and does not have that
        /// property.</para>
        /// </remarks>
        private IRngService TickRng(int tickIndex)
        {
            if (_rngService == null)
                return null;

            if (_tickRng == null || _tickRngIndex != tickIndex)
            {
                _tickRng = _rngService.GetStream(RngStream.Combat, tickIndex);
                _tickRngIndex = tickIndex;
            }

            return _tickRng;
        }
    }
}
