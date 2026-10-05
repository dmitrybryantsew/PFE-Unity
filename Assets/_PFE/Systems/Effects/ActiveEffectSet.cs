using System;
using System.Collections.Generic;
using PFE.Data.Definitions;
using PFE.ModAPI;

namespace PFE.Systems.Effects
{
    /// <summary>
    /// The set of live effects on one unit — the port of AS3's <c>Unit.effects</c> array together with
    /// <c>Unit.addEffect</c>, <c>Unit.remEffect</c> and the effect half of <c>Unit.step</c>.
    ///
    /// <para><b>Pure C#, no Unity and no MonoBehaviour.</b> The whole subsystem is unit-testable
    /// without an editor: the oracle's effect logic reads only integers, floats and strings, and the
    /// port keeps it that way. The one thing it deliberately does <i>not</i> own is the actual
    /// application of a payload (damage, healing, emitters) — that is a callback, because AS3 reaches
    /// into <c>this.owner.damage(...)</c> and the port must not let an effect set call a
    /// <c>MonoBehaviour</c> directly. See <see cref="IEffectHost"/>.</para>
    ///
    /// <para><b>Duration is counted in canonical 30 Hz ticks, not in port ticks.</b> AS3 decrements
    /// <c>t</c> once per <c>Unit.step</c>, and <c>Unit.step</c> runs once per flash frame at
    /// <c>World.fps = 30</c>. Effect data is authored against that rate — a <c>t='7'</c> burn is seven
    /// seconds at 30 ticks/second. The port runs a configurable tick rate, so <see cref="Tick"/>
    /// accumulates <see cref="PFE.Core.SimClock.StepScale"/> fractional ticks and fires the oracle's
    /// step only on whole canonical frames. Without this, the same burn would last 7s in a 30 Hz build
    /// and 1.75s in a 120 Hz one — a silent behaviour change with no error anywhere.</para>
    /// </summary>
    public sealed class ActiveEffectSet
    {
        /// <summary>AS3 <c>World.fps</c> — the rate the effect counter is authored against.</summary>
        public const int CanonicalTicksPerSecond = 30;

        /// <summary>
        /// AS3 <c>Unit.addEffect</c>'s clamp on a stacked duration (<c>Unit.as:3381-3384</c>):
        /// <c>if(t &gt; 30000) t = 30000</c>.
        /// </summary>
        public const int MaxStackedTicks = 30000;

        private readonly List<ActiveEffect> _effects = new List<ActiveEffect>();
        private readonly IEffectHost _host;
        private readonly IEffectDefinitionResolver _definitions;

        /// <summary>
        /// Fractional carry for the canonical-frame accumulator. See the class comment: the port's
        /// tick rate and the effect counter's rate are not the same thing.
        /// </summary>
        private float _tickCarry;

        /// <summary>
        /// The add-order list of live effects. Read-only to callers — every mutation goes through
        /// <see cref="AddEffect"/> or <see cref="RemoveEffect"/>, because the set has invariants (the
        /// three <c>tormoz</c>/<c>precMultCont</c>/<c>rapidMultCont</c> resets, the deferred-removal
        /// flag) that a caller writing the list directly would skip.
        /// </summary>
        public IReadOnlyList<ActiveEffect> Effects => _effects;

        public int Count => _effects.Count;

        /// <summary>
        /// Names of <c>&lt;sk&gt;</c> writes seen during the last param pass that had no registered
        /// setter. The oracle's <c>hasOwnProperty</c> guard makes an unmapped name a no-op, so this is
        /// fidelity rather than loss — but it is counted rather than discarded so the gap is visible
        /// instead of silent. The project's standing rule: never invent a field for an unmapped name.
        /// </summary>
        public IReadOnlyDictionary<string, int> UnmappedParamNames => _unmappedNames;

        private readonly Dictionary<string, int> _unmappedNames = new Dictionary<string, int>();

        public ActiveEffectSet(
            IEffectHost host,
            IEffectDefinitionResolver definitions)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        }

        // ── Add ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Apply an effect — the port of <c>Unit.addEffect(id, val, dur, announce)</c>
        /// (<c>Unit.as:3351-3404</c>).
        ///
        /// <para>The merge rules are the subtle part and they run in this order:</para>
        /// <list type="number">
        /// <item><b>tip 3 replaces wholesale.</b> If both the incoming effect and an existing one are
        /// <c>tip == 3</c> — the food/potion channel — the existing slot is <i>overwritten</i> and the
        /// function returns. Only one food buff can be active at a time, and the new one wins outright
        /// regardless of duration.</item>
        /// <item><b>Same id (or same post) merges.</b> A match on <c>id</c> <i>or on the existing
        /// effect's <c>post</c></i> reuses the slot. Note the second half: applying <c>rage</c> when a
        /// <c>post_rage</c> is pending replaces it, because the pending comedown belongs to the effect
        /// being re-applied.</item>
        /// <item><b>The higher value wins.</b> <c>if(existing.val &gt; incoming.val) incoming.val =
        /// existing.val</c> — so re-applying a weak hit never downgrades a strong one.</item>
        /// <item><b><c>add</c> sums the durations</b>, clamped to 30000, then re-runs <c>checkT</c> so
        /// the level can escalate from the longer duration. Without <c>add</c> the incoming duration
        /// simply replaces the old one — the oracle does <b>not</b> take the max, it takes the new
        /// value.</item>
        /// </list>
        ///
        /// <para>Returns the instance that now represents the effect — an existing one when merged, a
        /// new one otherwise, or <c>null</c> for a null/empty id (the oracle returns null early).</para>
        /// </summary>
        /// <param name="id">Effect id — <c>AllData.d.eff.(@id == id)</c>.</param>
        /// <param name="value">Payload override; <c>0</c> means "use the definition's <c>val</c>".</param>
        /// <param name="durationTicks">Duration override in canonical ticks; <c>0</c> means the definition's.</param>
        /// <param name="announce">Whether removal is announced to the player (<c>Effect.se</c>).</param>
        public ActiveEffect AddEffect(string id, float value = 0f, int durationTicks = 0, bool announce = true)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            IEffectTemplate definition = _definitions.Resolve(id);
            if (definition == null)
            {
                // The oracle would construct an Effect whose getXmlParam finds no node — leaving
                // t = 1 (its field default) and tip = 0, i.e. a one-tick, typeless phantom. That is
                // almost certainly a data bug, and the port reports it rather than materialising the
                // phantom, because a phantom effect is exactly the "plausible object with no
                // behaviour" this codebase keeps finding.
                return null;
            }

            var incoming = new ActiveEffect(
                definition,
                value,
                durationTicks > 0 ? durationTicks : (int?)null);

            for (int i = 0; i < _effects.Count; i++)
            {
                ActiveEffect existing = _effects[i];

                // 1. The food/potion channel: one at a time, the new one wins outright.
                if (incoming.Tip == EffectType.Food && existing.Tip == EffectType.Food)
                {
                    ReplaceSlot(i, incoming);
                    return incoming;
                }

                // 2. Same id, or the existing effect is the pending aftereffect of this one.
                if (existing.Id == id || existing.Definition.afterEffectId == id)
                {
                    // 3. The stronger payload survives.
                    if (existing.Value > incoming.Value)
                    {
                        incoming.Value = existing.Value;
                    }

                    // 4. `add` sums the durations; otherwise the new duration replaces the old.
                    if (definition.add)
                    {
                        int summed = incoming.TicksRemaining + existing.TicksRemaining;
                        incoming.TicksRemaining = summed > MaxStackedTicks ? MaxStackedTicks : summed;
                        CheckLevel(incoming);
                    }

                    ReplaceSlot(i, incoming);
                    return incoming;
                }
            }

            incoming.Announce = announce;
            _effects.Add(incoming);
            ApplyDeletes(incoming);
            _host.OnEffectStarted(incoming, this);
            return incoming;
        }

        private void ReplaceSlot(int index, ActiveEffect incoming)
        {
            _effects[index] = incoming;
            ApplyDeletes(incoming);
            _host.OnEffectStarted(incoming, this);
        }

        // ── Remove ────────────────────────────────────────────────────────────

        /// <summary>
        /// Remove every effect with this id — the port of <c>Unit.remEffect</c>
        /// (<c>Unit.as:3389-3398</c>).
        ///
        /// <para><b>Removal is deferred.</b> This does not take the effect out of the list. It marks it
        /// <c>vse</c> (being-unset), which keeps it in place for one more param pass so its writes can
        /// be undone by the reset-then-replay, and the actual splice happens in <see cref="Tick"/>.
        /// AS3's reasoning is the same: <c>unsetEff</c> sets <c>vse = true</c> and calls the param pass
        /// immediately, and the array is only filtered later. A port that removed eagerly would drop
        /// the effect's contribution before the pass that removes it — the value would be reset
        /// correctly (because the pass resets from baseline) but the <c>vse</c>-indexed read the oracle
        /// makes would be lost, and a <c>post</c> transition could not fire.</para>
        /// </summary>
        public void RemoveEffect(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return;
            }

            foreach (ActiveEffect effect in _effects)
            {
                if (effect != null && effect.Id == id)
                {
                    Unset(effect);
                }
            }
        }

        /// <summary>
        /// The <c>del</c> list: effects removed when another effect <b>starts</b> — AS3
        /// <c>Effect.setEff():143-156</c>, the mechanism that makes burning and freezing mutually
        /// exclusive.
        /// </summary>
        private void ApplyDeletes(ActiveEffect started)
        {
            string[] deletes = started.Definition.deletesOnStart;
            if (deletes == null || deletes.Length == 0)
            {
                return;
            }

            foreach (string id in deletes)
            {
                foreach (ActiveEffect effect in _effects)
                {
                    if (effect != null && effect.Id == id)
                    {
                        Unset(effect);
                        break;   // the oracle `break`s after the first match
                    }
                }
            }
        }

        /// <summary>
        /// Run <c>unsetEff</c>'s state machine on one instance (<c>Effect.as:282-403</c>): mark unset,
        /// run the param pass so the effect's writes are <b>undone immediately</b>, and — the surprising
        /// branch — <b>retarget to the aftereffect instead of removing</b> when the definition names a
        /// <c>post</c>/<c>postbad</c>.
        ///
        /// <para><b>This does not announce the end.</b> AS3's <c>unsetEff</c> only sets <c>vse</c>;
        /// the effect is not gone until the splice in <see cref="StepOnce"/>. The port fires
        /// <see cref="IEffectHost.OnEffectEnded"/> at the splice for the same reason — a
        /// <c>post</c>-retargeted effect clears <c>vse</c> and never leaves, so it correctly reports no
        /// end.</para>
        ///
        /// <para><b>The param pass here is load-bearing, and its absence was a real defect.</b>
        /// <c>Effect.as:337-348</c> —
        /// <c>if(this.params &amp;&amp; param3) { … setEffParams() / pers.setParameters() }</c> — runs the
        /// reset-then-replay <i>immediately</i> on unset, so the effect's numeric writ is removed at the
        /// moment it is unset. The port previously only marked <c>vse</c> and relied on the next payload
        /// tick to replay — but a <c>vse</c> effect is <b>spliced without being stepped</b> (see
        /// <see cref="StepOnce"/>), so a removed effect whose <c>t</c> is not a multiple of 30 was
        /// spliced before its replay ever ran, and its write was never undone. The value would keep a
        /// buff that no longer exists with nothing red anywhere. This call is what fixes that.</para>
        /// </summary>
        private void Unset(ActiveEffect effect)
        {
            if (effect.IsBeingUnset)
            {
                return;
            }

            effect.MarkUnset();

            string afterId = effect.Definition.afterEffectId;
            if (!string.IsNullOrEmpty(afterId))
            {
                IEffectTemplate afterDefinition = _definitions.Resolve(afterId);
                if (effect.TryTransitionToAfterEffect(afterDefinition))
                {
                    _host.OnEffectStarted(effect, this);
                    return;
                }
            }

            // The oracle's `if(this.params && param3)`: a param-carrying effect runs the pass on unset
            // so its contribution is removed now, not at some later payload tick (which may never come,
            // because a vse effect is spliced, not stepped). A param-less effect needs no pass.
            if (effect.HasParams)
            {
                _host.OnEffectParamsChanged(this);
            }
        }

        // ── Tick ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Advance the set by one simulation tick — the effect half of AS3 <c>Unit.step</c>.
        ///
        /// <para><paramref name="stepScale"/> is <see cref="PFE.Core.SimClock.StepScale"/>: how many
        /// canonical 30 Hz frames one port tick represents (<c>1</c> at 30 Hz, <c>0.25</c> at 120 Hz).
        /// The set accumulates it and runs the oracle's step once per whole canonical frame — see the
        /// class comment for why a burn must not run at the port's rate.</para>
        /// </summary>
        public void Tick(float stepScale = 1f)
        {
            if (stepScale <= 0f)
            {
                return;
            }

            _tickCarry += stepScale;

            // Bounded: a 120 Hz build accumulates 0.25 per tick and fires once every four ticks. The
            // guard is a belt against a pathological StepScale (a 0.1 Hz clock would loop 300 times per
            // tick) — the SimClock's supported rates never get near it.
            int framesToRun = (int)_tickCarry;
            if (framesToRun > CanonicalTicksPerSecond)
            {
                framesToRun = CanonicalTicksPerSecond;
            }
            _tickCarry -= framesToRun;

            for (int f = 0; f < framesToRun; f++)
            {
                StepOnce();

                if (_effects.Count == 0)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// One canonical frame — the effect half of AS3 <c>Unit.step</c>
        /// (<c>Unit.as:3144-3158</c>):
        ///
        /// <code>
        /// i = 0;
        /// while(i &lt; effects.length) {
        ///    if(!effects[i].vse) { effects[i].step(); }   // step only the LIVE ones
        ///    else { effects.splice(i, 1); i--; }          // a vse effect is dropped, never stepped
        ///    i++;
        /// }
        /// </code>
        ///
        /// and <c>Effect.step()</c> (<c>Effect.as:490-509</c>) itself:
        ///
        /// <code>
        /// if(this.t % 30 == 0) this.secEffect();   // the once-per-second payload
        /// this.stepEffect();                        // the every-frame payload
        /// --this.t;
        /// if(this.t &lt;= 0) {
        ///    if(this.forever) this.t = 30;
        ///    else this.unsetEff();
        /// }
        /// </code>
        ///
        /// <para><b>Order matters three times, and one of them was wrong in the first pass.</b></para>
        /// <list type="number">
        /// <item>The payload check reads <c>t</c> <i>before</i> the decrement, so a 30-tick effect
        /// fires at <c>t = 30</c> on its first frame. It fires <b>once</b>, not twice: by the time
        /// <c>t</c> reaches 0 on frame 30 the <c>t &lt;= 0</c> branch runs <c>unsetEff</c> in that same
        /// frame, and the effect never gets a <c>%30 == 0</c> check at <c>t = 0</c>. (An earlier
        /// version of this file claimed two fires — a documentation error, caught by
        /// <see cref="PFE.Tests.EditMode.Systems.Effects.ActiveEffectSetTests.Tick_FiresPayloadOnCanonicalSecondBoundaries"/>
        /// once the fixture could actually run.)</item>
        /// <item><b>Splice before step.</b> A <c>vse</c> effect is removed <i>without</i> being
        /// stepped — it does not get one more decrement, one more payload, or one more chance to
        /// expire. The first pass stepped then spliced, which would have given every removed effect an
        /// extra frame of life the oracle never granted.</item>
        /// <item>The splice happens <b>inside</b> the same loop, so an effect removed by <c>del</c>
        /// earlier in this frame is already gone and is not stepped.</item>
        /// </list>
        ///
        /// <para><b>Why <c>OnEffectEnded</c> fires here and not in <see cref="Unset"/>.</b> AS3 has no
        /// "effect ended" event — an effect has ended exactly when it leaves the array. <c>unsetEff</c>
        /// only sets <c>vse</c>; the removal is the splice. So the port announces the end where the
        /// oracle's effect actually disappears, which also means a <c>post</c>-retargeted effect (whose
        /// <c>vse</c> is cleared) correctly never reports an end at all.</para>
        /// </summary>
        private void StepOnce()
        {
            for (int i = 0; i < _effects.Count; i++)
            {
                ActiveEffect effect = _effects[i];
                if (effect == null)
                {
                    continue;
                }

                // The oracle's `else { splice(i,1); i--; }` — a vse effect is dropped, not stepped.
                if (effect.IsBeingUnset)
                {
                    _effects.RemoveAt(i);
                    i--;
                    _host.OnEffectEnded(effect, this);
                    continue;
                }

                if (effect.IsPayloadTick)
                {
                    // AS3 `secEffect()` opens with `checkT()` (`:408`), BEFORE any payload branch — and
                    // `checkT` is what turns the effect's CURRENT duration into its level (`:229-240`).
                    // The order is load-bearing rather than cosmetic: `drunk`'s payload is gated on
                    // `lvl > 3` (`:436`), so a level recomputed after the payload would leave that gate
                    // permanently false.
                    //
                    // It is called here, and not on add, because `t` only falls as the effect runs: the
                    // level is a function of the duration that is LEFT, so it must be recomputed on
                    // every payload tick rather than once at the start. Calling it only from the merge
                    // path — which is what this port did — left every freshly-added effect at level 1
                    // forever, making `drunk`'s escalation unreachable and its poison payload dead.
                    CheckLevel(effect);

                    _host.OnEffectPayload(effect, this);
                    if (effect.HasParams)
                    {
                        _host.OnEffectParamsChanged(this);
                    }
                }

                // AS3 `step()` (:490-497) runs `stepEffect()` on EVERY frame, after the `t % 30`
                // payload check and before the decrement. The order matters: `burning`'s flame reads
                // `sost` at the same moment the payload's damage lands, so drawing it before the
                // payload would show a frame of fire on a unit the burn is about to kill.
                _host.OnEffectStepVisual(effect, this);

                effect.TicksRemaining--;

                if (effect.TicksRemaining <= 0)
                {
                    if (effect.Forever)
                    {
                        effect.TicksRemaining = CanonicalTicksPerSecond;
                    }
                    else
                    {
                        // Marks vse and may retarget to the aftereffect (which clears vse, keeping the
                        // slot). A plain unset is spliced on the NEXT frame — that is the oracle's one
                        // frame of deferral, and it is why the param pass has a chance to run first.
                        Unset(effect);
                    }
                }
            }
        }

        // ── Level escalation ──────────────────────────────────────────────────

        /// <summary>
        /// Recompute an effect's <c>lvl</c> from its remaining duration — AS3 <c>Effect.checkT</c>
        /// (<c>Effect.as:222-253</c>):
        ///
        /// <code>
        /// if(lvl1 &gt; 0) {
        ///    lvl = 1;
        ///    if(t/30 &gt; lvl1) lvl = 2;
        ///    if(t/30 &gt; lvl2) lvl = 3;
        ///    if(t/30 &gt; lvl3) lvl = 4;
        ///    ...
        /// }
        /// </code>
        ///
        /// <para><b><c>lvl1 &gt; 0</c> is the gate, and that is why only <c>drunk</c> escalates.</b>
        /// It is the only definition carrying <c>lvl1</c>, so every other effect stays at level 1
        /// forever — the three thresholds are not "0 means immediately" but "0 means no escalation at
        /// all". Returns <c>true</c> when the level actually changed, which is when the oracle triggers
        /// a param pass.</para>
        /// </summary>
        public bool CheckLevel(ActiveEffect effect)
        {
            IEffectTemplate d = effect.Definition;
            if (d.lvl1 <= 0)
            {
                return false;
            }

            int previous = effect.Level;
            int seconds = effect.TicksRemaining / CanonicalTicksPerSecond;
            int level = 1;
            if (seconds > d.lvl1) level = 2;
            if (seconds > d.lvl2) level = 3;
            if (seconds > d.lvl3) level = 4;
            effect.Level = level;

            return level != previous;
        }

        // ── Query ─────────────────────────────────────────────────────────────

        /// <summary>True when an effect with this id is live (including one awaiting removal).</summary>
        public bool Has(string id)
        {
            foreach (ActiveEffect effect in _effects)
            {
                if (effect.Id == id)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The <c>&lt;sk&gt;</c> writes of every live effect, paired with the index the oracle would
        /// use for that effect — the input to the reset-then-replay param pass.
        ///
        /// <para><b>The index is the whole point of this method.</b> It is the oracle's own split:
        /// <list type="bullet">
        /// <item><c>PersMode.Npc</c> — index <c>1</c> while active, <c>0</c> while being unset
        /// (<c>Unit.as:3495</c>); removed effects are still yielded, because their <c>v0</c> value is
        /// what undoes their write.</item>
        /// <item><c>PersMode.Player</c> — index <c>eff.lvl</c> while active, and effects being unset
        /// are <b>skipped entirely</b> (<c>Pers.as:2202</c> replays the removed value never).</item>
        /// </list>
        /// A port that unified these would either lose the NPC reset or apply a value the player path
        /// never applies — see the design doc §3.</para>
        /// </summary>
        public IEnumerable<(EffectParam Param, int Index)> EnumerateParams(PersMode mode)
        {
            foreach (ActiveEffect effect in _effects)
            {
                if (effect.Definition == null || effect.Definition.effects == null)
                {
                    continue;
                }

                int index;
                if (mode == PersMode.Player)
                {
                    if (effect.IsBeingUnset)
                    {
                        continue;
                    }
                    index = effect.Level;
                }
                else
                {
                    index = effect.IsBeingUnset ? 0 : 1;
                }

                foreach (EffectParam param in effect.Definition.effects)
                {
                    yield return (param, index);
                }
            }
        }

        /// <summary>
        /// Record an unmapped <c>&lt;sk&gt;</c> name. Called by the param pass so the gap is counted
        /// rather than dropped — the oracle's <c>hasOwnProperty</c> guard makes it a no-op there too.
        /// </summary>
        public void RecordUnmappedName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            _unmappedNames.TryGetValue(name, out int n);
            _unmappedNames[name] = n + 1;
        }

        /// <summary>
        /// Drop everything and clear the tick accumulator — a fresh respawn. Does <b>not</b> fire
        /// end-of-effect callbacks: the oracle's teardown path for a new life does not unset each
        /// effect individually, it discards the array.
        /// </summary>
        public void Clear()
        {
            _effects.Clear();
            _tickCarry = 0f;
            _unmappedNames.Clear();
        }
    }
}
