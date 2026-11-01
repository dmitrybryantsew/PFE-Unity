using System;
using System.Collections.Generic;

namespace PFE.Sim.Units
{
    /// <summary>
    /// One spoken line — a <c>&lt;r&gt;</c> child of a <c>&lt;rep&gt;</c> row in the localisation
    /// document, plus the one attribute that changes whether it may be shown.
    /// </summary>
    /// <remarks>
    /// <para><b><c>m="1"</c> is not a variant, it is a gate.</b> In the <c>&lt;txt&gt;</c> tables the
    /// same attribute means "this text is a <c>mature|clean</c> pair, pick a half" (<c>Res.as:100-107</c>).
    /// In the <c>&lt;replic&gt;</c> table it means something else entirely: the line has no clean
    /// variant, so a build with the content filter on must <b>drop it</b> —
    /// <c>if(World.w.matFilter &amp;&amp; Boolean(xl[num].@m.length())) return "";</c>
    /// (<c>Res.as:299-302</c>). Same attribute, two different jobs; conflating them is how a filtered
    /// build would start swearing.</para>
    /// </remarks>
    public sealed class ReplicLine
    {
        /// <summary>The line's text, exactly as authored (entities decoded, whitespace trimmed).</summary>
        public readonly string Text;

        /// <summary>True for <c>&lt;r m="1"&gt;</c> — mature-only, dropped when the filter is on.</summary>
        public readonly bool MatureOnly;

        public ReplicLine(string text, bool matureOnly)
        {
            Text = text;
            MatureOnly = matureOnly;
        }
    }

    /// <summary>
    /// The enemy speech table — AS3 <c>Res.repText</c>'s data half
    /// (<c>Res.as:281-312</c>), parsed from the localisation document's
    /// <c>&lt;replic&gt;&lt;rep id=… act=…&gt;&lt;r&gt;…</c> block.
    ///
    /// <para><b>Where the lines live.</b> Not in <c>AllData.as</c>: they are player-facing text and ship
    /// in <c>Resources/Localisation/text_en.xml</c> — the same 800 KB document the PipBuck and
    /// <c>UnitNameTable</c> read. Measured on the shipped file: <b>102 <c>&lt;rep&gt;</c> rows</b>
    /// across 17 speaker ids and 24 acts, holding <b>982 <c>&lt;r&gt;</c> lines</b>.</para>
    ///
    /// <para><b>The speaker id is a <i>voice set</i>, not a unit id.</b> <c>&lt;vis replic='raider'/&gt;</c>
    /// on the <c>raider</c> row is inherited by all nine <c>raiderN</c> variants, and the robots share
    /// three ids between five bodies (<c>protect</c> for both Protectrons, <c>gutsy</c> for both Mr
    /// Gutsies, <c>sentinel</c> for the sentinel/ultra/mega trio). So the table is keyed by
    /// <c>(speaker, act)</c> and the <i>unit</i> half is a separate lookup.</para>
    ///
    /// <para><b>Unity-free on purpose.</b> This is the rules half — the caller reads the document and
    /// hands the text in, so an offline fixture can feed it the shipped file and pin the parsed counts
    /// without an editor. The Unity half is <c>ReplicCatalog</c>.</para>
    /// </summary>
    public sealed class ReplicTextTable
    {
        /// <summary>The element that opens the block — <c>&lt;replic&gt;</c>.</summary>
        private const string BlockOpen = "<replic>";

        /// <summary>The element that closes it.</summary>
        private const string BlockClose = "</replic>";

        /// <summary>Speaker id → act → lines, in document order.</summary>
        private readonly Dictionary<string, Dictionary<string, List<ReplicLine>>> _bySpeaker;

        /// <summary>An empty table — every lookup misses. Never null, so callers need no null checks.</summary>
        public static ReplicTextTable Empty { get; } =
            new ReplicTextTable(new Dictionary<string, Dictionary<string, List<ReplicLine>>>(StringComparer.Ordinal));

        private ReplicTextTable(Dictionary<string, Dictionary<string, List<ReplicLine>>> bySpeaker)
        {
            _bySpeaker = bySpeaker;
        }

        /// <summary>Number of <c>&lt;rep&gt;</c> rows parsed — 102 in the shipped document.</summary>
        public int RowCount { get; private set; }

        /// <summary>Number of <c>&lt;r&gt;</c> lines parsed — 982 in the shipped document.</summary>
        public int LineCount { get; private set; }

        /// <summary>Every speaker id the document authored, sorted — 17 in the shipped document.</summary>
        public IEnumerable<string> Speakers
        {
            get
            {
                var keys = new List<string>(_bySpeaker.Keys);
                keys.Sort(StringComparer.Ordinal);
                return keys;
            }
        }

        /// <summary>
        /// The lines for one <c>(speaker, act)</c> pair, or <c>false</c> when the document authored
        /// none — which is the common case: a raider has <c>neutral</c> and <c>attack</c> but no
        /// <c>hi</c>, and the oracle returns <c>""</c> for that.
        /// </summary>
        public bool TryGetLines(string speaker, string act, out IReadOnlyList<ReplicLine> lines)
        {
            lines = null;
            if (string.IsNullOrEmpty(speaker) || string.IsNullOrEmpty(act)) return false;
            if (!_bySpeaker.TryGetValue(speaker, out Dictionary<string, List<ReplicLine>> acts)) return false;
            if (!acts.TryGetValue(act, out List<ReplicLine> found) || found.Count == 0) return false;
            lines = found;
            return true;
        }

        /// <summary>
        /// True when the speaker exists at all — <c>&lt;vis replic='…'&gt;</c> naming an id the document
        /// never wrote. Used by the fixtures to tell "no such voice set" from "no such act", which look
        /// identical from a failed <see cref="TryGetLines"/>.
        /// </summary>
        public bool HasSpeaker(string speaker)
            => !string.IsNullOrEmpty(speaker) && _bySpeaker.ContainsKey(speaker);

        /// <summary>
        /// Parse the <c>&lt;replic&gt;</c> block out of a localisation document.
        ///
        /// <para><b>Why a scan and not an <c>XmlDocument</c>.</b> Same reason as
        /// <c>UnitNameTable.Parse</c>: the document is 13 000 lines the caller has already loaded as a
        /// string, and the block is a flat three-level list. A DOM would re-parse the whole file for
        /// one section.</para>
        ///
        /// <para><b>Attribute order is not assumed.</b> The shipped rows are written
        /// <c>&lt;rep act="neutral" id="raider" v="0.7"&gt;</c> — <c>act</c> first — while the AS3
        /// accessors are <c>@id</c> then <c>@act</c>. Reading by name rather than by position is what
        /// keeps a re-export with the attributes in the other order from silently emptying the
        /// table.</para>
        ///
        /// <para><b>A missing block is not an error.</b> A build with no speech table simply has
        /// silent enemies; every caller already handles "no line" because that is what the oracle does
        /// for an unknown act.</para>
        /// </summary>
        public static ReplicTextTable Parse(string xml)
        {
            var bySpeaker = new Dictionary<string, Dictionary<string, List<ReplicLine>>>(StringComparer.Ordinal);

            if (string.IsNullOrEmpty(xml)) return Empty;

            int blockStart = xml.IndexOf(BlockOpen, StringComparison.Ordinal);
            if (blockStart < 0) return Empty;

            int blockEnd = xml.IndexOf(BlockClose, blockStart, StringComparison.Ordinal);
            if (blockEnd < 0) blockEnd = xml.Length;

            int rows = 0;
            int lines = 0;

            int cursor = blockStart;
            while (true)
            {
                int repStart = xml.IndexOf("<rep ", cursor, StringComparison.Ordinal);
                if (repStart < 0 || repStart >= blockEnd) break;

                int openEnd = xml.IndexOf('>', repStart);
                if (openEnd < 0 || openEnd >= blockEnd) break;

                string attributes = xml.Substring(repStart + 5, openEnd - repStart - 5);
                string id = ReadAttribute(attributes, "id");
                string act = ReadAttribute(attributes, "act");

                int close = xml.IndexOf("</rep>", openEnd, StringComparison.Ordinal);
                if (close < 0 || close > blockEnd) break;

                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(act))
                {
                    List<ReplicLine> parsed = ReadLines(xml, openEnd + 1, close);
                    if (parsed.Count > 0)
                    {
                        if (!bySpeaker.TryGetValue(id, out Dictionary<string, List<ReplicLine>> acts))
                        {
                            acts = new Dictionary<string, List<ReplicLine>>(StringComparer.Ordinal);
                            bySpeaker[id] = acts;
                        }

                        // A second row for the same (speaker, act) appends rather than replaces: the
                        // oracle's `xl = d.replic[0].rep.(@id == id && @act == act)` takes xl[0], so a
                        // duplicate row would be shadowed — but silently dropping half the document is
                        // a worse failure than being generous, and the shipped file has no duplicates.
                        if (acts.TryGetValue(act, out List<ReplicLine> existing))
                        {
                            existing.AddRange(parsed);
                        }
                        else
                        {
                            acts[act] = parsed;
                        }

                        rows++;
                        lines += parsed.Count;
                    }
                }

                cursor = close + "</rep>".Length;
            }

            var table = new ReplicTextTable(bySpeaker) { RowCount = rows, LineCount = lines };
            return table;
        }

        /// <summary>Every <c>&lt;r&gt;</c> between <paramref name="start"/> and <paramref name="end"/>.</summary>
        private static List<ReplicLine> ReadLines(string xml, int start, int end)
        {
            var result = new List<ReplicLine>();

            int cursor = start;
            while (cursor < end)
            {
                int rStart = xml.IndexOf("<r", cursor, StringComparison.Ordinal);
                if (rStart < 0 || rStart >= end) break;

                // `<r>` and `<r m="1">` both exist. Anything else that starts with those two characters
                // is skipped rather than treated as the end of the row: aborting on it would silently
                // truncate every line after it.
                int afterTag = rStart + 2;
                if (afterTag < end && xml[afterTag] != '>' && xml[afterTag] != ' ')
                {
                    cursor = afterTag;
                    continue;
                }

                int openEnd = xml.IndexOf('>', rStart);
                if (openEnd < 0 || openEnd >= end) break;

                string attributes = xml.Substring(rStart + 2, openEnd - rStart - 2);

                // `Boolean(xl[num].@m.length())` (Res.as:299) — the oracle tests that the attribute is
                // PRESENT and non-empty, not that it equals "1". Every shipped row happens to write
                // m="1" (measured: 41 rows, no other value), so the two rules agree today; testing the
                // value would silently make a future m="0" line mature-in-the-oracle and clean here.
                bool matureOnly = !string.IsNullOrEmpty(ReadAttribute(attributes, "m"));

                int rEnd = xml.IndexOf("</r>", openEnd, StringComparison.Ordinal);
                if (rEnd < 0 || rEnd > end) break;

                string text = xml.Substring(openEnd + 1, rEnd - openEnd - 1).Trim();
                if (text.Length > 0)
                {
                    result.Add(new ReplicLine(DecodeEntities(text), matureOnly));
                }

                cursor = rEnd + 4;
            }

            return result;
        }

        /// <summary>
        /// The value of <paramref name="attribute"/> in an element's attribute text, or <c>null</c>.
        ///
        /// <para><b>The boundary check is load-bearing for one-character names.</b> <c>m</c> is a real
        /// attribute here (<c>&lt;r m="1"&gt;</c>) and so is <c>mod</c> on the dialogue rows, so a bare
        /// <c>IndexOf("m=")</c> would read a <c>mod</c> attribute's prefix as the mature flag the moment
        /// a row carried both. Requiring the name to start at the attribute run or after whitespace
        /// makes <c>m=</c> and <c>mod=</c> distinguishable.</para>
        /// </summary>
        private static string ReadAttribute(string attributes, string attribute)
        {
            int at = -1;
            int search = 0;
            while (search <= attributes.Length)
            {
                int candidate = attributes.IndexOf(attribute + "=", search, StringComparison.Ordinal);
                if (candidate < 0) break;

                bool startsWord = candidate == 0 || char.IsWhiteSpace(attributes[candidate - 1]);
                if (startsWord)
                {
                    at = candidate;
                    break;
                }

                search = candidate + 1;
            }

            if (at < 0) return null;

            int valueStart = at + attribute.Length + 1;
            if (valueStart >= attributes.Length) return null;

            char quote = attributes[valueStart];
            if (quote != '\'' && quote != '"') return null;

            int valueEnd = attributes.IndexOf(quote, valueStart + 1);
            if (valueEnd < 0) return null;

            return attributes.Substring(valueStart + 1, valueEnd - valueStart - 1).Trim();
        }

        /// <summary>
        /// The five predefined XML entities, decoded. The shipped <c>&lt;replic&gt;</c> block happens to
        /// contain none (measured: 0 of 982 lines), but it is the same document that carries escaped
        /// ampersands elsewhere and a re-export can introduce one.
        /// </summary>
        private static string DecodeEntities(string text)
        {
            if (text.IndexOf('&') < 0) return text;

            return text
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&quot;", "\"")
                .Replace("&apos;", "'")
                .Replace("&amp;", "&");
        }
    }
}
