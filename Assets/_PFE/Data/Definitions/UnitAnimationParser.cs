using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PFE.Data.Definitions
{
    /// <summary>
    /// Reads a unit's <c>&lt;blit&gt;</c> rows into an <see cref="AnimationSet"/> — the port of the
    /// loop at <c>Unit.as:1430-1436</c>, <c>anims[xbl.@id] = new BlitAnim(xbl)</c>.
    ///
    /// <para><b>Why this is a separate, pure type.</b> It lives in <c>PFE.Core</c> rather than beside
    /// <c>UnitDataImporter</c> in <c>PFE.Editor</c> because <c>PFE.Tests</c> references
    /// <c>PFE.Core</c> and not <c>PFE.Editor</c> — so a parser living in the importer could not be
    /// tested without the real <c>AllData.as</c> on disk. The importer calls this; the tests call
    /// this; neither needs a file.</para>
    ///
    /// <para><b>The family/variant join.</b> Animation data is split across two nodes. The family node
    /// (<c>raider</c>, <c>zombie</c>) declares the <c>&lt;blit&gt;</c> rows and no sheet; the variant node
    /// (<c>raider5</c>, <c>zombie3</c>) declares the sheet and, usually, no rows at all. The oracle joins
    /// them in the controller with a double call — <c>UnitAlicorn.as:233-234</c>
    /// <c>super.getXmlParam("alicorn"); super.getXmlParam();</c> — family first, own node second. Because
    /// <c>Unit.as:1430</c> assigns per id, the second pass <b>overlays</b> rather than replaces. Use the
    /// two-argument <see cref="Parse(string,string)"/> to reproduce that; the one-argument overload reads a
    /// single node and is what the tests use.</para>
    ///
    /// <para>The family id is always the unit's own <c>parent='…'</c> attribute, so the caller resolves it
    /// from data and this class needs no controller table.</para>
    /// </summary>
    public static class UnitAnimationParser
    {
        /// <summary>One <c>&lt;blit …/&gt;</c> row. Self-closing and paired forms both match.</summary>
        static readonly Regex BlitRow = new Regex(@"<blit\b([^>]*?)/?>", RegexOptions.Compiled);

        /// <summary>
        /// Every AS3 id that appears in <c>AllData.as</c> but has no field in
        /// <see cref="AnimationSet"/>. Named here so the importer's report can be specific rather than
        /// "some rows were skipped".
        /// </summary>
        public static readonly string[] KnownUnmappedIds = { "derg", "super", "attack" };

        /// <summary>What one parse produced.</summary>
        public sealed class Result
        {
            /// <summary>The states found, keyed by the AS3 ids <see cref="AnimationSet.TrySet"/> knows.</summary>
            public AnimationSet Animations = new AnimationSet();

            /// <summary>How many <c>&lt;blit&gt;</c> rows were seen at all.</summary>
            public int RowsRead;

            /// <summary>Ids with no field in <see cref="AnimationSet"/>, in first-seen order, de-duplicated.</summary>
            public List<string> UnmappedIds = new List<string>();

            /// <summary>
            /// The AS3 ids this pass actually declared a row for. Tracked explicitly rather than inferred
            /// from <c>HasFrames</c>, because <c>len='0'</c> is a legal row that would read as "absent".
            /// </summary>
            public HashSet<string> SetIds = new HashSet<string>();

            /// <summary>Rows that carried no usable <c>id</c>.</summary>
            public int RowsWithoutId;

            /// <summary>
            /// How many of <see cref="RowsRead"/> replaced a state the family node had already set.
            /// Non-zero only for the two-argument overload — this is the <c>zombie3</c> case, where the
            /// variant's single <c>pre</c> row overwrites the family's <c>pre</c>.
            /// </summary>
            public int RowsOverridingTemplate;

            /// <summary>True when at least one state was stored.</summary>
            public bool HasAnyState => SetIds.Count > 0;
        }

        /// <summary>
        /// Parse the <c>&lt;blit&gt;</c> rows out of a unit's XML.
        ///
        /// <para>The text may be a whole <c>&lt;unit&gt;…&lt;/unit&gt;</c> block or any fragment
        /// containing <c>&lt;blit&gt;</c> rows; only those rows are looked at.</para>
        /// </summary>
        public static Result Parse(string unitXml)
        {
            return Parse(unitXml, null);
        }

        /// <summary>
        /// Parse a unit's rows on top of its family node's rows — the oracle's
        /// <c>super.getXmlParam(family); super.getXmlParam();</c> pair.
        ///
        /// <para>The family's rows are laid down first and the unit's own rows <b>overwrite by id</b>,
        /// matching <c>Unit.as:1430-1436</c>, which assigns <c>anims[xbl.@id]</c> one id at a time and so
        /// leaves untouched ids alone. Pass <paramref name="templateXml"/> as <c>null</c> (or empty) when
        /// the unit is itself a family node, which is the <c>raider</c>/<c>alicorn</c>/<c>scorp1</c> case.</para>
        /// </summary>
        public static Result Parse(string unitXml, string templateXml)
        {
            var result = new Result();

            // Family pass first, so the unit's own rows win. Read into a scratch set rather than calling
            // Parse recursively, so RowsRead counts only the unit's own rows.
            if (!string.IsNullOrEmpty(templateXml))
            {
                Result family = ParseRows(templateXml);
                result.Animations = family.Animations;
                result.RowsWithoutId = family.RowsWithoutId;
                result.SetIds.UnionWith(family.SetIds);
                foreach (string id in family.UnmappedIds)
                {
                    if (!result.UnmappedIds.Contains(id))
                    {
                        result.UnmappedIds.Add(id);
                    }
                }
            }

            Result own = ParseRows(unitXml);

            result.RowsRead = own.RowsRead;
            result.RowsWithoutId += own.RowsWithoutId;
            result.SetIds.UnionWith(own.SetIds);
            foreach (string id in own.UnmappedIds)
            {
                if (!result.UnmappedIds.Contains(id))
                {
                    result.UnmappedIds.Add(id);
                }
            }

            foreach (string as3Id in AnimationSet.As3Ids)
            {
                if (!own.SetIds.Contains(as3Id))
                {
                    continue;
                }

                // Already present from the family pass => this row is a delta, not a new state.
                if (result.Animations.Get(as3Id).HasFrames)
                {
                    result.RowsOverridingTemplate++;
                }

                result.Animations.TrySet(as3Id, own.Animations.Get(as3Id));
            }

            // Ids the family pass stored that this pass did not touch are already in the set.
            return result;
        }

        /// <summary>Parse one node's rows into a fresh set. The shared body of both overloads.</summary>
        static Result ParseRows(string unitXml)
        {
            var result = new Result();
            if (string.IsNullOrEmpty(unitXml))
            {
                return result;
            }

            foreach (Match match in BlitRow.Matches(unitXml))
            {
                result.RowsRead++;

                string attrs = match.Groups[1].Value;
                string id = Attribute(attrs, "id");

                if (string.IsNullOrEmpty(id))
                {
                    result.RowsWithoutId++;
                    continue;
                }

                var frame = new AnimationFrame
                {
                    // AS3 `BlitAnim` defaults: id = 0, maxf = 1, firstf = 0, retf = 0, df = 1.
                    row = IntAttribute(attrs, "y", 0),
                    length = IntAttribute(attrs, "len", 1),
                    firstFrame = IntAttribute(attrs, "ff", 0),
                    returnFrame = IntAttribute(attrs, "rf", 0),
                    frameStep = FloatAttribute(attrs, "df", 1f),

                    // `rep` and `stab` are PRESENCE tests, not value tests:
                    // BlitAnim.as:3703 `if(param1.@rep.length()) this.replay = true;`
                    // so rep='0' means true. Parsing the value would silently drop the loop — and this
                    // is not hypothetical: AllData.as has three `rep='0'` rows on `stay`.
                    replay = HasAttribute(attrs, "rep"),
                    isStatic = HasAttribute(attrs, "stab")
                };

                if (result.Animations.TrySet(id, frame))
                {
                    result.SetIds.Add(id);
                }
                else if (!result.UnmappedIds.Contains(id))
                {
                    result.UnmappedIds.Add(id);
                }
            }

            return result;
        }

        // ── attribute helpers ────────────────────────────────────────────────
        //
        // AS3 authors single-quoted attributes throughout AllData.as. Double quotes are accepted too
        // so a hand-written test fixture or a future data file is not silently read as empty.

        static string Attribute(string attrs, string name)
        {
            // The leading (^|\s) matters: without it `id` would also match inside `uid`, and `y` inside
            // any lower-case name ending in y. A substring hit here reads the wrong attribute's value
            // and is invisible — the row still parses, it just animates the wrong cells.
            Match m = Regex.Match(attrs, @"(^|\s)" + name + @"\s*=\s*'([^']*)'");
            if (m.Success)
            {
                return m.Groups[2].Value;
            }

            m = Regex.Match(attrs, @"(^|\s)" + name + "\\s*=\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[2].Value : string.Empty;
        }

        static bool HasAttribute(string attrs, string name)
        {
            return Regex.IsMatch(attrs, @"(^|\s)" + name + @"\s*=");
        }

        static int IntAttribute(string attrs, string name, int fallback)
        {
            string raw = Attribute(attrs, name);
            return int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int value)
                ? value
                : fallback;
        }

        static float FloatAttribute(string attrs, string name, float fallback)
        {
            string raw = Attribute(attrs, name);
            return float.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float value)
                ? value
                : fallback;
        }
    }
}
