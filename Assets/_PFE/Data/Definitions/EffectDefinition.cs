using UnityEngine;
using PFE.ModAPI;
#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif

namespace PFE.Data.Definitions
{
    /// <summary>
    /// ScriptableObject definition for one <c>&lt;eff&gt;</c> template from <c>AllData.as</c> —
    /// the port of AS3's effect data, consumed by the runtime status-effect system.
    ///
    /// <para><b>This type was rewritten on 2026-10-03</b> because the first version could not express
    /// what <c>Effect.getXmlParam</c> (<c>Effect.as:67-137</c>) actually reads. In particular it stored
    /// the raw <c>t</c> attribute in a field called <c>durationTicks</c> (the oracle multiplies by 30),
    /// carried no way to express the <c>&lt;sk&gt;</c> writes at all, and had no <c>del</c>/<c>lvl</c>/
    /// <c>add</c>/<c>him</c> surface. Nothing consumed it, so the loss was invisible — the same "a
    /// belief that went stale while nothing went red" shape this project keeps hitting.</para>
    ///
    /// <para><b>Duration is in ticks and is the ×30 value.</b> <c>Effect.as:82</c> reads
    /// <c>this.t = node.@t * 30</c>. The asset stores that already-multiplied tick count so consumers
    /// never re-derive it and cannot disagree about the factor. A template with <b>no</b> <c>t</c> is
    /// <b>permanent</b>: <c>if(this.t == 0) { this.t = 30; this.forever = true; }</c> (<c>:132-136</c>),
    /// flagged by <see cref="forever"/>.</para>
    /// </summary>
    [CreateAssetMenu(fileName = "NewEffectDef", menuName = "PFE/Effect Definition")]
    public class EffectDefinition : ScriptableObject, IGameContent, IEffectTemplate
    {
        [Header("Identity")]
        [Tooltip("Unique ID for this effect — AS3 <eff id>.")]
        public string effectId;

        // IGameContent
        string IGameContent.ContentId => effectId;
        ContentType IGameContent.ContentType => ContentType.Effect;

        // Legacy property for compatibility
        public string ID => effectId;

#if ODIN_INSPECTOR
        [BoxGroup("Core")]
#else
        [Header("Core")]
#endif
        [Tooltip("Effect category — AS3 <eff tip>. 3 is the food/potion channel, of which only one may " +
                 "be active per unit at a time (see ActiveEffectSet.Add).")]
        public EffectType type = EffectType.Neutral;

        [Tooltip("Duration in TICKS — already the AS3 @t * 30 value. Ignored when 'forever' is true.")]
        public int durationTicks = 30;

        /// <summary>
        /// True for a template with no <c>t</c> attribute — AS3's
        /// <c>if(this.t == 0) { this.t = 30; this.forever = true; }</c> (<c>Effect.as:132-136</c>).
        /// A permanent effect's <c>t</c> reloops in 30-tick windows rather than expiring
        /// (<c>:498-508</c>), which is what makes its once-per-second payload keep firing.
        /// </summary>
        [Tooltip("Permanent effect (AS3: template had no @t). Never expires; its 30-tick payload " +
                 "reloops for as long as it is active.")]
        public bool forever = false;

        [Tooltip("Base value — AS3 <eff val>, used by the DoT payloads (the per-second damage amount).")]
        public float value = 0f;

#if ODIN_INSPECTOR
        [BoxGroup("Stat writes")]
#else
        [Header("Stat writes")]
#endif
        [Tooltip("The <sk> children — the stat writes this effect performs while active.")]
        public EffectParam[] effects;

#if ODIN_INSPECTOR
        [BoxGroup("Duration escalation")]
#else
        [Header("Duration escalation")]
#endif
        [Tooltip("AS3 <eff lvl1> — at t/30 > lvl1 the effect's level rises to 2. 0 = no escalation.")]
        public int lvl1 = 0;

        [Tooltip("AS3 <eff lvl2> — level rises to 3.")]
        public int lvl2 = 0;

        [Tooltip("AS3 <eff lvl3> — level rises to 4.")]
        public int lvl3 = 0;

#if ODIN_INSPECTOR
        [BoxGroup("Merge / aftereffect")]
#else
        [Header("Merge / aftereffect")]
#endif
        /// <summary>
        /// AS3 <c>&lt;eff add/&gt;</c> — when present, re-applying this effect while it is already
        /// active <b>adds</b> the remaining durations instead of taking the longer of the two
        /// (<c>Unit.as:3377-3385</c>), clamped to 30000 ticks.
        /// </summary>
        [Tooltip("Stacking: re-application ADDS duration (AS3 <eff add/>), instead of keeping the " +
                 "longer remaining duration.")]
        public bool add = false;

        [Tooltip("Effect ID to apply when this one expires — AS3 <eff post>.")]
        public string afterEffectId;

        /// <summary>
        /// True when the aftereffect came from AS3 <c>postbad</c> rather than <c>post</c>.
        /// <c>getXmlParam</c> reads <c>post</c> first and then <b>overwrites</b> it from <c>postbad</c>
        /// if that attribute exists (<c>Effect.as:95-103</c>) — so a node carrying both ends up on the
        /// bad one. <c>unsetEff</c> uses the flag to seed addiction severity (<c>:316-334</c>).
        /// </summary>
        [Tooltip("Aftereffect came from AS3 'postbad' (the comedown variant).")]
        public bool afterIsBad = false;

        [Tooltip("AS3 <eff him> — the 'him' counter seed. 0 when absent.")]
        public int him = 0;

        /// <summary>
        /// AS3 <c>&lt;del id&gt;</c> children — effects to remove when this one <b>starts</b>
        /// (<c>Effect.as:143-156</c>). This is the mutual-exclusion mechanism: burning deletes
        /// freezing, freezing deletes burning.
        /// </summary>
        [Tooltip("Effect IDs removed when this effect starts — AS3 <del id>.")]
        public string[] deletesOnStart;

        [Header("Display")]
        [Tooltip("Display name. The oracle's real name comes from a localisation table " +
                 "(Res.txt(\"e\", id)) the port does not have, so the importer stores the raw id here " +
                 "rather than inventing a label.")]
        public string displayName;

        // ── IEffectTemplate ───────────────────────────────────────────────────
        //
        // Explicit implementations rather than converting the public fields to properties: Unity's
        // serializer only persists *fields*, so the Inspector-facing surface must stay as fields.
        // The interface is what the engine-free runtime sees, and every member forwards to the field,
        // so there is exactly one storage location and no way for the two to drift apart (the
        // "declaration is not the definition" trap this project keeps hitting).

        string IEffectTemplate.effectId => effectId;
        EffectType IEffectTemplate.type => type;
        int IEffectTemplate.durationTicks => durationTicks;
        bool IEffectTemplate.forever => forever;
        float IEffectTemplate.value => value;
        bool IEffectTemplate.add => add;
        string IEffectTemplate.afterEffectId => afterEffectId;
        int IEffectTemplate.lvl1 => lvl1;
        int IEffectTemplate.lvl2 => lvl2;
        int IEffectTemplate.lvl3 => lvl3;
        string[] IEffectTemplate.deletesOnStart => deletesOnStart;
        EffectParam[] IEffectTemplate.effects => effects;
    }
}
