using System;
using System.Collections.Generic;
using System.Xml.Linq;

namespace PFE.Sim.Units
{
    /// <summary>
    /// The unit id → voice set map — AS3 <c>Unit.id_replic</c>, resolved the way
    /// <c>UnitRaider.getXmlParam</c> resolves it: family row first, variant row second.
    ///
    /// <para><b>Why the inheritance is real and not a convenience.</b> Only the family rows carry
    /// <c>&lt;vis replic='…'&gt;</c>: <c>&lt;unit id='raider'&gt;&lt;vis replic='raider'/&gt;</c>. The
    /// nine <c>raiderN</c> variants are separate rows with no <c>&lt;vis&gt;</c> of their own, and the
    /// oracle gets the value across by calling <c>super.getXmlParam(this.parentId)</c> <i>before</i>
    /// <c>super.getXmlParam()</c> (<c>UnitRaider.as:296-298</c>): the family pass writes
    /// <c>id_replic</c>, the variant pass has nothing to overwrite it with. A lookup that only read the
    /// unit's own row would leave every raider in the game mute except the one body literally called
    /// <c>raider</c>.</para>
    ///
    /// <para><b>Unity-free, so the walk is pinned by a fixture</b> rather than by reading the code. The
    /// document reading lives in <c>ReplicCatalog</c>; this type takes three strings per unit and
    /// answers one question.</para>
    /// </summary>
    public sealed class ReplicSpeakerMap
    {
        /// <summary>Depth guard for a malformed <c>@parent</c> chain that points at itself.</summary>
        private const int MaxParentDepth = 8;

        /// <summary>Unit id → voice set id, for the ids that resolve.</summary>
        private readonly Dictionary<string, string> _speakerByUnit;

        /// <summary>Unit id → its <c>@parent</c>, for the ids that have one.</summary>
        private readonly Dictionary<string, string> _parentByUnit;

        /// <summary>Unit ids already walked and found to have no voice set.</summary>
        private readonly HashSet<string> _silentUnits;

        /// <summary>An empty map — every lookup misses. Never null, so callers need no null checks.</summary>
        public static ReplicSpeakerMap Empty { get; } = new ReplicSpeakerMap(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));

        private ReplicSpeakerMap(Dictionary<string, string> speakerByUnit, Dictionary<string, string> parentByUnit)
        {
            _speakerByUnit = speakerByUnit;
            _parentByUnit = parentByUnit;
            _silentUnits = new HashSet<string>(StringComparer.Ordinal);
        }

        /// <summary>How many unit ids name a voice set directly (not counting inherited ones).</summary>
        public int DirectCount => _speakerByUnit.Count;

        /// <summary>
        /// Build from <c>(id, parent, replic)</c> rows. <paramref name="replic"/> empty means the row
        /// names no voice set; <paramref name="parent"/> empty means it has no ancestor.
        /// </summary>
        public static ReplicSpeakerMap FromRows(IEnumerable<(string Id, string Parent, string Replic)> rows)
        {
            var speakers = new Dictionary<string, string>(StringComparer.Ordinal);
            var parents = new Dictionary<string, string>(StringComparer.Ordinal);

            if (rows != null)
            {
                foreach ((string id, string parent, string replic) in rows)
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    if (!string.IsNullOrEmpty(parent)) parents[id] = parent;
                    if (!string.IsNullOrEmpty(replic)) speakers[id] = replic;
                }
            }

            return new ReplicSpeakerMap(speakers, parents);
        }

        /// <summary>
        /// Build from an <c>AllData</c> root — the <c>&lt;all&gt;</c> element, whose
        /// <c>&lt;unit&gt;</c> children each carry <c>@id</c>, an optional <c>@parent</c> and an
        /// optional <c>&lt;vis replic='…'&gt;</c>.
        ///
        /// <para><b>Here rather than in the catalog</b>, because the walk below is what the fixture
        /// pins and this is the reader that feeds it: a catalog-only version would let the fixture
        /// prove <see cref="FromRows"/> while the shipped path took a different route. Unity-free, so
        /// the offline harness can compile it and read the real 148-row document.</para>
        /// </summary>
        public static ReplicSpeakerMap FromUnitRoot(XElement root)
        {
            if (root == null) return Empty;

            var rows = new List<(string Id, string Parent, string Replic)>();
            foreach (XElement unit in root.Elements("unit"))
            {
                string id = (string)unit.Attribute("id");
                if (string.IsNullOrEmpty(id)) continue;

                rows.Add((id, (string)unit.Attribute("parent"), (string)unit.Element("vis")?.Attribute("replic")));
            }

            return FromRows(rows);
        }

        /// <summary>
        /// The voice set for <paramref name="unitId"/>, or <c>null</c> when the unit row (or any of its
        /// ancestors) names none.
        /// </summary>
        public string SpeakerFor(string unitId)
        {
            if (string.IsNullOrEmpty(unitId)) return null;
            if (_speakerByUnit.TryGetValue(unitId, out string direct)) return direct;
            if (_silentUnits.Contains(unitId)) return null;

            string current = unitId;
            for (int depth = 0; depth < MaxParentDepth; depth++)
            {
                if (!_parentByUnit.TryGetValue(current, out string parent) || string.IsNullOrEmpty(parent)) break;
                if (_speakerByUnit.TryGetValue(parent, out string inherited))
                {
                    // Cache the answer on the asking id: the walk is per-variant, the answer is per-family.
                    _speakerByUnit[unitId] = inherited;
                    return inherited;
                }

                current = parent;
            }

            _silentUnits.Add(unitId);
            return null;
        }
    }
}
