using System;
using PFE.Data.Definitions;

namespace PFE.Systems.Effects
{
    /// <summary>
    /// One <b>live</b> status effect on a unit — the port of AS3's <c>fe.unit.Effect</c>
    /// (<c>Effect.as</c>), whose per-instance state is created by the <c>Effect(param1, param2,
    /// param3)</c> constructor and then mutated by <c>step()</c>.
    ///
    /// <para><b>Definition vs. instance.</b> <see cref="EffectDefinition"/> is the immutable template
    /// (<c>AllData.d.eff.(@id == id)</c>); this is the mutable runtime copy the oracle keeps in
    /// <c>Unit.effects</c>. The split matters because the oracle's <c>Effect</c> <i>copies</i> the
    /// template's <c>t</c>, <c>val</c> and <c>lvl</c> at construction and then counts them down — the
    /// template is never touched. A port that mutated the ScriptableObject would corrupt every other
    /// unit sharing that id.</para>
    ///
    /// <para><b>Duration is in canonical 30 Hz ticks.</b> AS3 counts <c>t</c> down once per
    /// <c>Unit.step</c>, i.e. once per 30 Hz flash frame, and the data's <c>@t</c> is seconds
    /// (<c>this.t = node.@t * 30</c>, <c>Effect.as:82</c>). The field here therefore holds the
    /// already-multiplied value and is decremented by <see cref="ActiveEffectSet"/> at the canonical
    /// rate regardless of the port's configured tick rate — see that class for why counting in
    /// SimClock ticks instead would make a burn's duration frame-rate dependent.</para>
    /// </summary>
    public sealed class ActiveEffect
    {
        /// <summary>The template this instance was built from. Never mutated by the runtime, but
        /// <b>replaced</b> in place by a <c>post</c> transition — the oracle re-reads the new id's XML
        /// into the same object (<c>Effect.as:317</c>).
        ///
        /// <para>Typed as the engine-free <see cref="IEffectTemplate"/>, not the
        /// <c>ScriptableObject</c> — see that interface for why the runtime must not name a Unity
        /// type.</para></summary>
        public IEffectTemplate Definition { get; private set; }

        /// <summary>AS3 <c>Effect.id</c> — copied from the definition so a <c>post</c> transition can
        /// retarget the instance without losing the "what is this now" answer.</summary>
        public string Id { get; private set; }

        /// <summary>AS3 <c>Effect.tip</c> — the category, read from the definition.</summary>
        public EffectType Tip => Definition != null ? Definition.type : EffectType.Neutral;

        /// <summary>
        /// AS3 <c>Effect.t</c> — remaining ticks. Counted down once per canonical frame.
        /// </summary>
        public int TicksRemaining { get; set; }

        /// <summary>
        /// AS3 <c>Effect.forever</c>. A permanent effect reloops <c>t</c> to 30 instead of expiring
        /// (<c>Effect.as:498-508</c>).
        /// </summary>
        public bool Forever { get; private set; }

        /// <summary>
        /// AS3 <c>Effect.lvl</c> — the escalation level 1..4, recomputed by
        /// <see cref="ActiveEffectSet.CheckLevel"/> as the duration grows (<c>Effect.as:222-253</c>).
        ///
        /// <para><b>Read by the player param pass only.</b> The NPC replay hardcodes index 1; only
        /// <c>Pers.setParameters</c> passes <c>eff.lvl</c> (<c>:2202</c>). So this field is live for a
        /// <c>CharacterStats</c>-owned effect set and inert for a <c>UnitStats</c>-owned one — which is
        /// the oracle's split, not a port gap.</para>
        /// </summary>
        public int Level { get; internal set; } = 1;

        /// <summary>
        /// AS3 <c>Effect.val</c> — the payload amount (DoT damage per second, etc.). Seeded from the
        /// caller's override when non-zero, else from the definition
        /// (<c>if(this.val == 0) this.val = node.@val</c>, <c>Effect.as:87-90</c>).
        /// </summary>
        public float Value { get; set; }

        /// <summary>
        /// AS3 <c>Effect.vse</c> — "the effect is being unset". Set by <see cref="Unset"/> and
        /// honoured by the param pass, which is the whole point of the flag: the effect keeps its
        /// place in the array for one more pass so its writes can be <i>undone</i> by replay, and the
        /// set removes it on the next tick (<c>Effect.as:301</c>, <c>Unit.as:3459</c>).
        /// </summary>
        public bool IsBeingUnset { get; private set; }

        /// <summary>
        /// AS3 <c>Effect.se</c> — whether removal is announced to the player in the message log.
        /// </summary>
        public bool Announce { get; set; } = true;

        /// <summary>
        /// True when the definition carries any <c>&lt;sk&gt;</c> writes — AS3 <c>Effect.params</c>,
        /// which gates whether a param pass is triggered at all (<c>Effect.as:91-94</c>).
        /// </summary>
        public bool HasParams => Definition != null && Definition.effects != null && Definition.effects.Length > 0;

        /// <summary>
        /// True when <see cref="Unset"/> retargeted this instance to its aftereffect. The oracle keeps
        /// the same <c>Effect</c> object and just rewrites <c>id</c> + re-reads the XML
        /// (<c>Effect.as:313-336</c>), which is why a "remove" can leave a live effect behind.
        /// </summary>
        public bool HasTransitioned => !string.Equals(Id, OriginalId, StringComparison.Ordinal);

        /// <summary>The id the instance was created with — for diagnostics and <see cref="HasTransitioned"/>.</summary>
        public string OriginalId { get; }

        internal ActiveEffect(IEffectTemplate definition, float valueOverride, int? durationOverride)
        {
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            Id = definition.effectId;
            OriginalId = definition.effectId;

            // AS3: new Effect(id, owner, val) sets val = param3, then getXmlParam only fills it from
            // the node when the caller passed 0. A caller-supplied duration is applied afterwards
            // (addEffect's param3, Unit.as:3360-3363: `t = param3 * fps`).
            Value = valueOverride != 0f ? valueOverride : definition.value;

            Forever = definition.forever;
            TicksRemaining = durationOverride ?? definition.durationTicks;

            // A caller-supplied duration overrides `forever`: addEffect writes t from param3 while
            // leaving the forever flag alone, so a called-with-duration permanent effect still
            // reloops (Effect.as:498-508 keys on `forever`, not on how t was set).
        }

        /// <summary>
        /// The duration to use when this instance is merged into an existing one — AS3 reads
        /// <c>this.effects[i].t</c> from the *existing* effect, which is why the merge rule
        /// <c>_loc5_.t += this.effects[_loc6_].t</c> (<c>Unit.as:3381</c>) needs this rather than the
        /// fresh value.
        /// </summary>
        internal int DurationForMerge => TicksRemaining;

        /// <summary>
        /// Mark this effect as being unset — AS3 <c>Effect.unsetEff</c>'s first act
        /// (<c>Effect.as:301</c>). Idempotent: calling it twice must not re-run a <c>post</c>
        /// transition, because the oracle's <c>post</c> branch would then retarget the already-
        /// retargeted id.
        /// </summary>
        internal void MarkUnset()
        {
            IsBeingUnset = true;
        }

        /// <summary>
        /// Apply the <c>post</c>/<c>postbad</c> aftereffect transition — AS3
        /// <c>Effect.unsetEff():313-336</c>:
        ///
        /// <code>
        /// if(post &amp;&amp; param1) {                       // param1 = "run the transition"
        ///    this.id = this.post;
        ///    this.getXmlParam();                      // re-read the NEW id's definition, in place
        ///    if(postBad) { ...addiction severity... }
        ///    this.vse = false;                        // switched back to ACTIVE
        /// }
        /// </code>
        ///
        /// <para><b>The instance survives, and the transition is a re-initialisation.</b> "Removing" an
        /// effect whose definition names a <c>postbad</c> does not remove anything — it replaces the
        /// effect in place with the comedown variant, re-reads the new template (so the new <c>t</c>,
        /// <c>val</c>, <c>forever</c> and <c>&lt;sk&gt;</c> set all apply), and clears <c>vse</c>, so
        /// the next param pass applies the new id's writes. The array slot is unchanged.</para>
        ///
        /// <para>Returns <c>true</c> when a transition happened, so the set can skip its own removal
        /// bookkeeping. Does <b>not</b> touch the addiction table (<c>addictions</c>/<c>ad1..ad3</c>):
        /// the port has no addiction model, which is recorded as a divergence in the design doc — the
        /// comedown therefore runs at whatever <c>lvl</c> the remap left, rather than being promoted by
        /// addiction severity. <paramref name="afterDefinition"/> carries <c>postbad</c>'s own
        /// <c>tip</c>/<c>t</c>/<c>val</c> exactly as AS3's <c>getXmlParam</c> would read them.</para>
        /// </summary>
        internal bool TryTransitionToAfterEffect(IEffectTemplate afterDefinition)
        {
            if (afterDefinition == null)
            {
                return false;
            }

            Definition = afterDefinition;
            Id = afterDefinition.effectId;

            // getXmlParam resets these before reading the new node (Effect.as:71-77): t, post,
            // postBad, del, him, lvl, forever. val is only filled when it was 0 — and a transition
            // keeps the instance's existing val, so the oracle does *not* overwrite a non-zero val.
            Forever = afterDefinition.forever;
            TicksRemaining = afterDefinition.durationTicks;
            Level = 1;
            if (Value == 0f)
            {
                Value = afterDefinition.value;
            }

            IsBeingUnset = false;
            return true;
        }

        /// <summary>
        /// AS3 <c>Effect.secEffect()</c>'s per-second branch condition: the payload fires when
        /// <c>t % 30 == 0</c> (<c>Effect.as:492</c>). Exposed because the port's ticker must ask the
        /// same question of the same counter.
        /// </summary>
        public bool IsPayloadTick => TicksRemaining % ActiveEffectSet.CanonicalTicksPerSecond == 0;

        public override string ToString()
        {
            return Id + " (t=" + TicksRemaining + (Forever ? ", forever" : "") +
                   ", lvl=" + Level + ", vse=" + IsBeingUnset + ")";
        }
    }
}
