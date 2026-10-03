namespace PFE.Data.Definitions
{
    /// <summary>
    /// The <b>read surface</b> the status-effect runtime needs from an effect template — everything
    /// <c>Effect.getXmlParam</c> (<c>Effect.as:67-137</c>) would have read from the <c>&lt;eff&gt;</c>
    /// node.
    ///
    /// <para><b>Why this is not <see cref="EffectDefinition"/> directly.</b> The runtime
    /// (<see cref="PFE.Systems.Effects.ActiveEffectSet"/>) is pure C#: it counts ticks, merges
    /// durations, and reads integers — none of which needs an engine. But
    /// <see cref="EffectDefinition"/> is a <c>ScriptableObject</c>, and a <c>ScriptableObject</c>
    /// subclass can <b>only</b> be instantiated inside a running editor: both
    /// <c>CreateInstance</c> and even <c>RuntimeHelpers.GetUninitializedObject</c> fail outside it
    /// (<c>SecurityException: ECall methods must be packaged into a system module</c> — the second
    /// returns <c>null</c> silently). A test that must build a definition therefore cannot run
    /// headless, which is how 846 of this project's tests ended up unable to go red.</para>
    ///
    /// <para>Splitting the <i>data</i> the runtime reads from the <i>asset</i> the editor authors lets
    /// the runtime stay engine-free and lets a fixture supply a plain object. The asset keeps its
    /// behaviour, the runtime keeps its purity, and a test's arrange step no longer depends on the
    /// editor being present — so an assertion can actually fail.</para>
    /// </summary>
    public interface IEffectTemplate
    {
        /// <summary>AS3 <c>&lt;eff id&gt;</c> — the unique id, and the key a resolver looks up.</summary>
        string effectId { get; }

        /// <summary>AS3 <c>&lt;eff tip&gt;</c> — the category (see <see cref="EffectType"/>).</summary>
        EffectType type { get; }

        /// <summary>Duration in canonical 30 Hz ticks — already the <c>@t * 30</c> value.</summary>
        int durationTicks { get; }

        /// <summary>True when the template had no <c>t</c> (permanent; reloops instead of expiring).</summary>
        bool forever { get; }

        /// <summary>AS3 <c>&lt;eff val&gt;</c> — the payload amount.</summary>
        float value { get; }

        /// <summary>AS3 <c>&lt;eff add/&gt;</c> — re-application adds duration instead of replacing.</summary>
        bool add { get; }

        /// <summary>AS3 <c>&lt;eff post&gt;</c>/<c>&lt;postbad&gt;</c> — the aftereffect id, or null.</summary>
        string afterEffectId { get; }

        /// <summary>AS3 <c>&lt;eff lvl1..3&gt;</c> escalation thresholds; 0 means no escalation.</summary>
        int lvl1 { get; }
        int lvl2 { get; }
        int lvl3 { get; }

        /// <summary>AS3 <c>&lt;del id&gt;</c> children — effects removed when this one starts.</summary>
        string[] deletesOnStart { get; }

        /// <summary>AS3 <c>&lt;sk&gt;</c> children — the stat writes this effect performs while active.</summary>
        EffectParam[] effects { get; }
    }
}
