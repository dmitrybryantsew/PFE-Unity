using System.Collections.Generic;
using PFE.Systems.Particles;
using UnityEngine;

namespace PFE.Data.Definitions
{
    /// <summary>
    /// The 118 <c>&lt;part&gt;</c> rows from <c>AllData.as</c>, as one asset — the port of AS3's
    /// <c>AllData.d.part</c> and the <c>Emitter.arr</c> index built from it (<c>Emitter.as:129-139</c>).
    ///
    /// <para><b>One database asset rather than 118 per-row assets.</b> The rows are never referenced
    /// individually by an asset path — they are looked up by <b>id</b> at runtime, exactly like the
    /// oracle's <c>arr[id]</c> — so the per-definition asset pattern used for weapons and effects (where
    /// each row is a thing you can open and edit) buys nothing here and costs 118 files. The
    /// <c>SkillDefinitionDatabase</c> precedent is the same shape.</para>
    ///
    /// <para><b>The table is built lazily and cached.</b> The dictionary is a runtime index, not
    /// serialised data, so it is rebuilt after a domain reload; if <see cref="definitions"/> is ever
    /// edited in place the cache goes stale, which is why the importer always assigns a fresh array
    /// rather than mutating one.</para>
    /// </summary>
    [CreateAssetMenu(fileName = "ParticleDefinitions", menuName = "PFE/Particle Definitions")]
    public class ParticleDefinitionAsset : ScriptableObject
    {
        /// <summary>
        /// The rows, in <c>AllData.as</c> file order. Order is not significant for lookup — the table
        /// indexes by id — but it is kept stable so a re-import diffs cleanly.
        /// </summary>
        [Tooltip("The <part> rows from AllData.as, in file order. Written by " +
                 "'PFE/Data/Import Particles from AllData.as'.")]
        public ParticleDefinition[] definitions = System.Array.Empty<ParticleDefinition>();

        /// <summary>
        /// Attribute names present in the data that <c>Emitter</c> has no field for, and which the
        /// oracle therefore discards silently. Recorded at import time so the divergence is visible in
        /// the Inspector rather than only in a log line that has already scrolled away.
        ///
        /// <para>Against the shipped data this is <c>rr</c> (7 rows) and <c>rd</c> (3 rows) — and
        /// <c>rr</c> is documented in the block's own comment as "случайная скорость вращения", random
        /// rotation speed, so the data's author believed it worked. It never has. See
        /// <see cref="ParticleXmlReader"/>.</para>
        /// </summary>
        [Tooltip("Attributes in the source that Emitter has no field for, so the oracle drops them. " +
                 "Non-empty means the data and the code disagree.")]
        public string[] ignoredAttributes = System.Array.Empty<string>();

        /// <summary>Attributes whose value could not be parsed as the declared type. Empty for the shipped data.</summary>
        [Tooltip("Attributes present but unparseable, which fell back to their defaults.")]
        public string[] malformedAttributes = System.Array.Empty<string>();

        private ParticleDefinitionTable _table;

        /// <summary>
        /// The id → definition index. Built once on first access and cached.
        /// </summary>
        public ParticleDefinitionTable Table =>
            _table ?? (_table = new ParticleDefinitionTable(definitions));

        /// <summary>Drops the cached index. Call after changing <see cref="definitions"/> in place.</summary>
        public void InvalidateTable() => _table = null;

        /// <summary>Number of rows. Zero means the importer has not been run.</summary>
        public int Count => definitions?.Length ?? 0;
    }
}
