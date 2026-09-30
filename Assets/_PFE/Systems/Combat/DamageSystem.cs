using System.Collections.Generic;
using UnityEngine;
using VContainer.Unity;
using MessagePipe;
using PFE.Core;
using PFE.Core.Messages;
using PFE.Core.Rng;
using PFE.Data.Definitions;
using PFE.Entities.Units;
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
    /// <para><b>Timing is a flag, the formula is not.</b> <c>PfeDebugSettings.SimTickDamage</c> chooses
    /// <i>when</i> a reported hit resolves — on the tick, or immediately at report time. It does not
    /// choose the formula; that is now armour-aware in both modes. With no unit carrying armour the two
    /// modes produce the same numbers, because the armour terms are inert, so the flag is safe to
    /// flip while play-testing.</para>
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
    /// tick and its queue. It is fetched <b>once per tick</b> and shared by that tick's hits, because
    /// <c>PcgRngService.GetStream(stream, salt)</c> constructs a fresh generator per call — fetching
    /// per hit would hand every hit in the tick an identical stream, and therefore identical crit
    /// rolls. In immediate mode there is no tick index to salt with, so the persistent <c>Combat</c>
    /// child stream is used.</para>
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
        /// Records a hit for resolution. In tick mode this only queues; in immediate mode it resolves
        /// on the spot. Either way the caller does not learn the damage — a source reports, it does not
        /// read back.
        /// </summary>
        public void Report(in PendingDamage hit)
        {
            if (hit.Target == null)
                return;

            if (IsTickAligned)
            {
                _pending.Add(hit);
                return;
            }

            Resolve(hit, ImmediateRng());
        }

        public void SimTick(int tickIndex)
        {
            if (_pending.Count == 0)
                return;

            // Swap first: anything reported during the drain below must land in the fresh list.
            (_draining, _pending) = (_pending, _draining);
            _pending.Clear();

            // One stream per tick, shared by every hit in it. See the class remarks — salting per hit
            // would hand each hit a fresh, identical generator.
            IRngService rng = _rngService?.GetStream(RngStream.Combat, tickIndex);

            int count = _draining.Count;
            for (int i = 0; i < count; i++)
            {
                Resolve(_draining[i], rng);
            }

            _draining.Clear();
        }

        // ── Resolution ───────────────────────────────────────────────────────

        private void Resolve(in PendingDamage hit, IRngService rng)
        {
            IDamageable target = hit.Target;

            // Re-checked at drain time, not just at report time: an earlier hit in this same drain may
            // have killed it, and the GameObject may be gone entirely.
            if (IsGone(target) || !target.IsAlive)
            {
                SkippedCount++;
                return;
            }

            DamageContext ctx = hit.Context;

            // ── Hit avoidance ────────────────────────────────────────────────────────────────────
            // AS3 `Unit.udarBullet():4067-4131` gates the ENTIRE hit on a four-term conjunction, and a
            // missed shot does nothing at all: no damage, no armour wear, no crit, no knockback, no
            // floating damage number. So this runs before every damage term rather than as a zeroing
            // term inside them — a miss that still wore the armour would be a different game.
            //
            // Two exclusions, both the oracle's:
            //   * a blast never rolls. AS3's explosion path (`Bullet.explRun`) calls `unit.damage()`
            //     directly and never reaches `udarBullet`, so `miss`/`precision`/`dodge` do not apply
            //     to AoE at all.
            //   * the roll uses the same per-tick stream as the armour and crit rolls, in the oracle's
            //     order. `HitAvoidance.RollsHit` reproduces AS3's short-circuits exactly, so a shot
            //     with `miss = 0` against a non-evading target consumes no roll here and the crit
            //     stream stays where AS3 would leave it.
            if (!hit.IsExplosion
                && !HitAvoidance.RollsHit(ctx, target.Evasion, hit.TravelDistancePixels, rng))
            {
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

                return;
            }

            float incoming = hit.IsExplosion
                ? ExplosionDamageFor(ctx, hit)
                : ctx.BaseDamage;

            if (incoming <= 0f)
                return;

            // ── Damage spread ───────────────────────────────────────────────────────────────────
            // AS3 `Unit.as:4085` — `_loc4_ = param1.damage * (Math.random() * 0.6 + 0.7)`, inside the
            // `if (param1.damage > 0)` block, so it sits AFTER that gate (hence below, not above) and
            // BEFORE `this.damage()`, whose body applies vulnerability then armour then crit. So the
            // spread lands on the pre-armour number, and crit amplifies the spread value — the
            // oracle's order.
            //
            // Blasts are excluded: AS3's explosion path (`Bullet.explRun`) calls `unit.damage()`
            // directly and never reaches `udarBullet`, so an explosion does its falloff damage exactly.
            //
            // The draw happens even under the testDam debug toggle — `DamageVariance.Roll` takes the
            // roll first and only then decides whether to report it — because the combat stream is
            // shared with the armour-reliability and crit rolls below. Skipping the draw would shift
            // every later roll in the same tick, which is a replication bug, not a debug convenience.
            if (!hit.IsExplosion)
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
            if (incoming <= 0f)
                return;

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
                skinResistance: target.SkinResistance);

            target.ApplyDamage(outcome);
            ResolvedCount++;

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
        }

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
    }
}
