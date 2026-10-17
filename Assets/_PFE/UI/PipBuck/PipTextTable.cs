using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace PFE.UI.PipBuck
{
    /// <summary>
    /// The Oracle's localisation document — the port of <c>fe.Res</c>'s lookup half
    /// (<c>Res.as:55-147</c>) over the XML <c>fe.TextLoader</c> loads (<c>World.as:531-539</c>).
    ///
    /// <para><b>Why this exists at all.</b> Every caption the PipBuck shows is a string-table lookup
    /// in the Oracle, not a literal: <c>PipBuck.as:172</c> writes <c>Res.pipText("main" + n)</c> for
    /// the tab bar, <c>PipPageInv.as:60</c> writes <c>Res.pipText("inv" + page2)</c> for the sub-tabs,
    /// <c>:308</c> writes <c>Res.pipText("emptyinv")</c>, and the inventory header keys are
    /// <c>ii1</c>…<c>ii6</c>. The document those keys index was <b>never in the Oracle tree</b> —
    /// which is why the port's captions were read off a screenshot and why one of them
    /// (<c>main4</c>) was recorded as "unobserved, so not invented". It is in the shipping build:
    /// <c>P:\SteamLibrary\steamapps\common\Remains\text_en.xml</c>, the file <c>lang.xml</c> names for
    /// <c>en</c>. That file now ships with the project at
    /// <c>Assets/_PFE/Data/Resources/Localisation/text_en.xml</c>.</para>
    ///
    /// <para><b>Unity-free on purpose.</b> No <c>UnityEngine</c> — the same reason
    /// <see cref="PipInventoryList"/> is: the rules below branch (two documents, a table alias, a
    /// child-index, a missing-key marker) and branching rules are exactly what a compile cannot
    /// check. A plain class can be executed from a shell against the shipped XML.</para>
    ///
    /// <para><b>The boundary: this returns what the Oracle's <c>xl[clob[razd]][0]</c> holds, and
    /// nothing more.</b> <c>Res.txt</c> does more than look a string up — it substitutes key names
    /// (<c>addKeys</c>, <c>:362-378</c>) and rewrites <c>[</c>/<c>]</c> into <c>&lt;span&gt;</c>
    /// markup (<c>:124-127</c>). Those passes belong to <c>PFE.UI.Common.PfeTextFormatter</c>, which
    /// already ports them, and duplicating them here would give the port two formatters that can
    /// drift. So a caller that wants markup formats the result; a caller that wants the caption the
    /// Oracle shows raw — which is what <c>pipText</c> returns at its default <c>razd = 0</c>, and
    /// why <c>main0</c> really does read <c>Close [TAB]</c> with the brackets intact — uses this
    /// directly.</para>
    /// </summary>
    public sealed class PipTextTable
    {
        /// <summary>
        /// One spoken line of a dialogue — a <c>&lt;r&gt;</c> row under an entry's <c>&lt;n&gt;</c>.
        ///
        /// <para><b>This is what makes a Littlepip message read as Littlepip.</b> The rows carry
        /// <c>p="lp1"…"lp4"</c> for the player character, <c>"pip"</c> for the PipBuck and <c>"mtn"</c>
        /// for a MAS terminal (<c>GUI.as:1833-1846</c>), and <c>begDial</c>'s three lines are all
        /// <c>p="lp1"</c> — the opening monologue. A port that flattened the rows to one string would
        /// still show the words, with no way to say who is speaking.</para>
        /// </summary>
        public readonly struct DialogueRow
        {
            /// <summary>The <c>p</c> attribute — the speaker's portrait id, or <c>null</c> when the row
            /// is unattributed narration.</summary>
            public readonly string Speaker;

            /// <summary>The row's text, with <c>@n</c> key substitution applied.</summary>
            public readonly string Text;

            /// <summary>AS3 <c>@mod</c>: 0 = the dialogue window, ≥1 = the information panel,
            /// 2 = the panel with an OK button (<c>GUI.as:1789-1900</c>).</summary>
            public readonly int Mod;

            /// <summary>AS3 <c>@push &gt; 0</c> — append to the current text rather than replace
            /// it.</summary>
            public readonly bool Push;

            public DialogueRow(string speaker, string text, int mod, bool push)
            {
                Speaker = speaker;
                Text = text;
                Mod = mod;
                Push = push;
            }

            /// <summary>True when this row names a speaker at all.</summary>
            public bool HasSpeaker => !string.IsNullOrEmpty(Speaker);
        }

        // ── The Oracle's alias table ──────────────────────────────────────────

        /// <summary>
        /// <c>Res.clob</c> (<c>Res.as:17-31</c>) — the one-letter table aliases every call site uses.
        /// <c>Res.pipText(id)</c> is <c>Res.txt("p", id)</c>, so "p" is <c>pip</c> and not "pip".
        ///
        /// <para>The same object also carries integer keys (<c>clob[0] = "n"</c> …), which are the
        /// <c>razd</c> child indices rather than tables. <see cref="ChildFor"/> holds those.</para>
        /// </summary>
        private static readonly Dictionary<string, string> TableAliases =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "u", "unit" },
                { "w", "weapon" },
                { "a", "armor" },
                { "o", "obj" },
                { "i", "item" },
                { "e", "eff" },
                { "f", "info" },
                { "p", "pip" },
                { "k", "key" },
                { "g", "gui" },
                { "m", "map" },
            };

        /// <summary><c>clob[0..3]</c> — the child element a <c>razd</c> selects (<c>Res.as:28-31</c>).</summary>
        private static readonly string[] Children = { "n", "info", "mess", "help" };

        /// <summary>
        /// The Oracle's table name for a one-letter alias, or <c>null</c> when the alias is not in
        /// <c>Res.clob</c>. Returning null rather than echoing the alias back is deliberate: an
        /// unknown alias must not silently become a table name that happens to exist.
        /// </summary>
        public static string TableFor(string alias)
        {
            return alias != null && TableAliases.TryGetValue(alias, out string table) ? table : null;
        }

        /// <summary>
        /// The child element a <c>razd</c> selects — <c>n</c>, <c>info</c>, <c>mess</c> or
        /// <c>help</c> (<c>Res.as:28-31</c>), or <c>null</c> when out of range. The Oracle would throw
        /// on an out-of-range <c>razd</c> and swallow it (<c>Res.as:73-75</c>); the port says "no such
        /// child" instead, which is the same answer without the exception.
        /// </summary>
        public static string ChildFor(int razd)
        {
            return razd >= 0 && razd < Children.Length ? Children[razd] : null;
        }

        /// <summary>
        /// <c>World.w.matFilter</c> — the Oracle's "show the mature variant" switch, read in
        /// <c>Res.txt</c> when an entry carries <c>m="1"</c> and its text is a
        /// <c>|</c>-separated pair.
        ///
        /// <para>Static because the Oracle's is: it is one world-level flag read from a static
        /// lookup. Defaults to <c>false</c>, which is the Oracle's own start and the branch a
        /// content-filtered build takes. It is a settable property rather than a constant so the
        /// branch is <i>ported</i> instead of dropped — the port has no settings screen to flip it
        /// yet.</para>
        /// </summary>
        public static bool MatureContentFilter { get; set; }

        // ── Instance ──────────────────────────────────────────────────────────

        /// <summary>
        /// Table name → entry id → child element name → text. The Oracle's <c>Res.d</c> /
        /// <c>Res.e</c>, flattened.
        /// </summary>
        private readonly Dictionary<string, Dictionary<string, Entry>> _active;
        private readonly Dictionary<string, Dictionary<string, Entry>> _fallback;

        /// <summary>Total entries parsed, so an importer or a verify step can assert a count.</summary>
        public int Count { get; private set; }

        /// <summary>True when no document was supplied — every lookup misses.</summary>
        public bool IsEmpty => Count == 0;

        private PipTextTable(
            Dictionary<string, Dictionary<string, Entry>> active,
            Dictionary<string, Dictionary<string, Entry>> fallback)
        {
            _active = active;
            _fallback = fallback;
            Count = CountEntries(active);
        }

        /// <summary>
        /// A table with no document. Every lookup misses and <see cref="Text"/> returns the Oracle's
        /// own missing-key marker — which is what the port should do if the XML is absent, rather
        /// than substituting English prose and calling it a translation.
        /// </summary>
        public static PipTextTable Empty { get; } = new PipTextTable(
            new Dictionary<string, Dictionary<string, Entry>>(StringComparer.Ordinal),
            new Dictionary<string, Dictionary<string, Entry>>(StringComparer.Ordinal));

        /// <summary>One entry's child elements, by child name.</summary>
        private sealed class Entry
        {
            public readonly Dictionary<string, string> Children =
                new Dictionary<string, string>(StringComparer.Ordinal);

            /// <summary>The Oracle's <c>@m</c> — this text has a mature variant after a <c>|</c>.</summary>
            public bool HasMatureVariant;

            /// <summary>
            /// The entry element's own <c>s1</c>…<c>s5</c> attributes — the key names <c>@1</c>…<c>@5</c>
            /// stand for. Held as a 5-slot array with <c>null</c> for an absent slot, so slot <i>i</i> is
            /// always <c>Keys[i - 1]</c> and an absent attribute is distinguishable from an empty one.
            ///
            /// <para><b>Both the entry and its rows carry these, and they are not the same table.</b>
            /// <c>&lt;txt id="trWalk" s1="keyLeft" s2="keyRight"&gt;&lt;n&gt;Press @1 or @2…</c> puts them on
            /// the <i>entry</i>, because the body is a plain <c>n</c> node
            /// (<c>Res.messText</c>, <c>Res.as:243-253</c>). A dialogue row puts them on the <i>row</i>:
            /// <c>&lt;r p="term" s1="keyPip"&gt;…@1…</c> (<c>GUI.dialText</c>, <c>GUI.as:1810-1818</c>).
            /// Reading only one of the two is why a port can show "Press @1 to move" for ever.</para>
            /// </summary>
            public string[] Keys;

            /// <summary>The Oracle's <c>@imp</c> — 0 hides the message when the caller is not
            /// important (<c>Res.messText</c>, <c>Res.as:174-178</c>).</summary>
            public int Importance;

            /// <summary>The <c>n</c> child's <c>&lt;r&gt;</c> rows, in document order. Empty when the
            /// entry's body is plain text rather than a list of spoken lines.</summary>
            public List<RawLine> Rows;

            /// <summary>
            /// The <c>&lt;n&gt;</c> child's <b>own</b> children, by element name, in document order —
            /// <c>&lt;t&gt;</c> titles above all.
            ///
            /// <para><b>Why the flattened <see cref="Children"/> entry is not enough.</b>
            /// <c>Children["n"]</c> is <c>InnerText</c>, which concatenates a note's <c>&lt;t&gt;</c>
            /// title and every <c>&lt;r&gt;</c> row into one run with no separator. A note's <i>title</i>
            /// is specifically <c>xml.n.t[0]</c> (<c>PipPageInfo.as:216</c>), and a help note's title is
            /// <c>ADVANCED CONTROLS</c> — a string that cannot be recovered from the flattened form
            /// without guessing where it ends.</para>
            /// </summary>
            public Dictionary<string, List<string>> NChildren;
        }

        /// <summary>
        /// A row exactly as the document holds it — <c>@n</c> placeholders <b>not</b> yet replaced.
        ///
        /// <para>The substitution needs <c>Res.txt("k", id)</c>, which is this same table, so it cannot
        /// run during <c>ParseDocument</c> without recursing into a half-built index. Keeping the raw
        /// form and resolving on read also means a caller that wants the key <i>ids</i> rather than their
        /// captions can still get them.</para>
        ///
        /// <para><b>Public because <c>Res.messText</c> needs the raw form too.</b>
        /// <c>TryGetDialogueRow</c> resolves the keys through <see cref="ApplyKeys"/>, which substitutes
        /// the bare key <i>name</i>; the Oracle's message body wraps that name in
        /// <c>&lt;span class='yel'&gt;</c> instead (<c>Res.as:202-213</c>), so a caller porting
        /// <c>messText</c> has to see the placeholder to place the markup. See
        /// <see cref="TryGetRawRow"/>.</para>
        /// </summary>
        public readonly struct RawLine
        {
            /// <summary>The row's <c>@p</c> — a speaker id, empty for narration.</summary>
            public readonly string Speaker;

            /// <summary>The row's text, with <c>@1</c>…<c>@5</c> still in place and <c>[br]</c>
            /// already turned into a newline by <see cref="CleanText"/>.</summary>
            public readonly string Text;

            /// <summary>The row's own <c>s1</c>…<c>s5</c> key names, or <c>null</c>.</summary>
            public readonly string[] Keys;

            /// <summary>The row's <c>@mod</c>.</summary>
            public readonly int Mod;

            /// <summary>The row's <c>@push</c>.</summary>
            public readonly bool Push;

            /// <summary>
            /// The row's <c>@m</c> — the text after a <c>|</c> is the mature variant
            /// (<c>Res.messText</c>, <c>Res.as:187-201</c>).
            ///
            /// <para>23 of the 1930 <c>&lt;r&gt;</c> rows in the shipped document carry it, so the
            /// branch is live and not decorative.</para>
            /// </summary>
            public readonly bool Mature;

            public RawLine(string speaker, string text, string[] keys, int mod, bool push, bool mature)
            {
                Speaker = speaker;
                Text = text;
                Keys = keys;
                Mod = mod;
                Push = push;
                Mature = mature;
            }
        }

        // ── Parsing ───────────────────────────────────────────────────────────

        /// <summary>
        /// Parses the Oracle's localisation document.
        ///
        /// <para><b>Why two documents.</b> <c>Res.txt</c> looks in <c>Res.d</c> first and falls back
        /// to <c>Res.e</c> (<c>Res.as:70-86</c>). <c>World.as:531-539</c> makes <c>Res.e</c> the
        /// <i>default</i> language's document and <c>Res.d</c> the selected one — and when the two are
        /// the same language it assigns the same <c>TextLoader</c> to both, so the fallback is a
        /// no-op. The port ships one language, so <paramref name="fallbackXml"/> defaults to the
        /// active document and the fallback becomes the Oracle's no-op rather than a missing
        /// feature.</para>
        /// </summary>
        /// <param name="xml">The selected language's document — the Oracle's <c>Res.d</c>.</param>
        /// <param name="fallbackXml">The default language's document — the Oracle's <c>Res.e</c>.</param>
        public static PipTextTable Parse(string xml, string fallbackXml = null)
        {
            if (string.IsNullOrWhiteSpace(xml)) return Empty;

            Dictionary<string, Dictionary<string, Entry>> active = ParseDocument(xml);
            Dictionary<string, Dictionary<string, Entry>> fallback =
                fallbackXml == null ? active : ParseDocument(fallbackXml);

            return new PipTextTable(active, fallback);
        }

        /// <summary>
        /// Parses a document from disk. <c>File.ReadAllText</c> and not a <c>TextAsset</c>, so this
        /// stays callable from the offline harness — the same reason <c>SourceLint.ProjectRoot</c>
        /// avoids <c>Application.dataPath</c>.
        /// </summary>
        public static PipTextTable ParseFile(string path, string fallbackPath = null)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Empty;
            return Parse(File.ReadAllText(path), fallbackPath != null && File.Exists(fallbackPath)
                ? File.ReadAllText(fallbackPath)
                : null);
        }

        /// <summary>
        /// Walks the document. <b>The root's direct children are the entries, and an entry's element
        /// name is its table</b> — there is no grouping level:
        /// <c>&lt;all&gt;&lt;pip id="main2"&gt;&lt;n&gt;Inventory&lt;/n&gt;&lt;/pip&gt;&lt;pip id="main3"&gt;…&lt;/pip&gt;&lt;eff id="pier"&gt;…&lt;/eff&gt;&lt;/all&gt;</c>.
        /// The table is therefore the tag (<c>pip</c>, <c>eff</c>, <c>unit</c>…), which is exactly
        /// what <c>Res.clob</c> maps a one-letter alias onto, and the child elements (<c>n</c>,
        /// <c>info</c>, <c>mess</c>, <c>help</c>) are the <c>razd</c> fields.
        ///
        /// <para><b>Entries with no <c>id</c> are skipped, and that is not an oversight.</b> The one
        /// such table is <c>advice</c>, whose <c>&lt;a&gt;</c> children are addressed by <i>position</i>
        /// (<c>Res.advText</c>, <c>Res.as:266-271</c>) rather than by id. A positional accessor is a
        /// different lookup and gets its own port if it is ever needed; folding it in here would make
        /// this class's contract "id or index" and lose the ability to say which.</para>
        /// </summary>
        private static Dictionary<string, Dictionary<string, Entry>> ParseDocument(string xml)
        {
            var tables = new Dictionary<string, Dictionary<string, Entry>>(StringComparer.Ordinal);

            var doc = new XmlDocument { XmlResolver = null };
            doc.LoadXml(xml);

            XmlNode root = doc.DocumentElement;
            if (root == null) return tables;

            foreach (XmlNode entryNode in root.ChildNodes)
            {
                if (entryNode.NodeType != XmlNodeType.Element) continue;

                var element = (XmlElement)entryNode;
                string id = element.GetAttribute("id");
                if (string.IsNullOrEmpty(id)) continue;

                // The tag IS the table name: `<pip id="main2">` is a row of the `pip` table.
                string table = element.Name;

                if (!tables.TryGetValue(table, out Dictionary<string, Entry> entries))
                {
                    entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
                    tables[table] = entries;
                }

                var entry = new Entry
                {
                    // Res.as:100 — `if(xl.@m == "1")`. Only the exact string counts, which is why
                    // this compares rather than parses.
                    HasMatureVariant = element.GetAttribute("m") == "1",
                    Keys = ReadKeys(element),
                    Importance = ReadInt(element.GetAttribute("imp")),
                };

                foreach (XmlNode child in element.ChildNodes)
                {
                    if (child.NodeType != XmlNodeType.Element) continue;
                    entry.Children[child.Name] = child.InnerText;

                    // The `<r>` rows live under `<n>` and are what `GUI.dialText` walks by index
                    // (GUI.as:1770-1782). `InnerText` above flattens them into one run of prose with no
                    // separator, so keeping the rows is the only way to answer "what does line 1 say".
                    if (child.Name == "n")
                    {
                        entry.Rows = ReadRows(child);
                        entry.NChildren = ReadGrandchildren(child);
                    }
                }

                entries[id] = entry;
            }

            return tables;
        }

        /// <summary>
        /// Reads the entry element's <c>s1</c>…<c>s5</c> key names into a fixed 5-slot array.
        /// <c>null</c> for an absent slot — see <see cref="Entry.Keys"/> for why the two carriers are
        /// not interchangeable.
        /// </summary>
        private static string[] ReadKeys(XmlElement element)
        {
            string[] keys = null;
            for (int i = 1; i <= 5; i++)
            {
                string value = element.GetAttribute("s" + i);
                if (string.IsNullOrEmpty(value)) continue;

                keys ??= new string[5];
                keys[i - 1] = value;
            }

            return keys;
        }

        /// <summary>Reads an <c>&lt;n&gt;</c> child's <c>&lt;r&gt;</c> rows, or <c>null</c> when the
        /// body is plain text.</summary>
        private static List<RawLine> ReadRows(XmlNode nNode)
        {
            List<RawLine> rows = null;

            foreach (XmlNode child in nNode.ChildNodes)
            {
                if (child.NodeType != XmlNodeType.Element || child.Name != "r") continue;

                var row = (XmlElement)child;
                rows ??= new List<RawLine>();
                rows.Add(new RawLine(
                    speaker: row.GetAttribute("p"),
                    text: CleanText(row.InnerText),
                    keys: ReadKeys(row),
                    mod: ReadInt(row.GetAttribute("mod")),
                    push: ReadInt(row.GetAttribute("push")) > 0,
                    // `if(node.@m.length())` — Res.as:187. A presence test on the attribute, not on
                    // its value: `<r m='1'>` and `<r m='0'>` both take the `|` split.
                    mature: row.HasAttribute("m")));
            }

            return rows;
        }

        private static int ReadInt(string raw) =>
            int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                         System.Globalization.CultureInfo.InvariantCulture, out int value)
                ? value
                : 0;

        /// <summary>
        /// The <c>&lt;n&gt;</c> node's own children, grouped by element name and kept in document order
        /// — the <c>xml.n.t</c> / <c>xml.n.r</c> lists the Oracle indexes into
        /// (<c>PipPageInfo.as:216-223</c>). <c>null</c> when <c>&lt;n&gt;</c> has no element children.
        ///
        /// <para>Text is run through <see cref="CleanText"/>, the same pass the rows get, so a title
        /// arrives with its indentation collapsed rather than with the source file's pretty-printing
        /// baked in. Note that this is a port decision: the Oracle's <c>node.toString()</c> would keep
        /// the raw whitespace, and Flash's text field then folds it during layout. A Unity label does
        /// not fold, which is the same reason <see cref="CleanText"/> exists for rows.</para>
        /// </summary>
        private static Dictionary<string, List<string>> ReadGrandchildren(XmlNode nNode)
        {
            Dictionary<string, List<string>> children = null;

            foreach (XmlNode child in nNode.ChildNodes)
            {
                if (child.NodeType != XmlNodeType.Element) continue;

                children ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
                if (!children.TryGetValue(child.Name, out List<string> texts))
                {
                    texts = new List<string>();
                    children[child.Name] = texts;
                }

                texts.Add(CleanText(child.InnerText));
            }

            return children;
        }

        /// <summary>
        /// The Oracle's <c>s.replace(/[\b\r\t]/g,"")</c> plus its <c>[br]</c> → line break
        /// (<c>Res.as:124</c>, <c>:240</c>), and the newline/indent runs the XML pretty-printer leaves
        /// inside a multi-line <c>&lt;r&gt;</c>.
        ///
        /// <para><b>Whitespace collapse is the port's addition, and it is needed.</b> A row written
        /// across several source lines (<c>begDialCP</c>'s terminal speech is) arrives with the
        /// indentation baked in; the Oracle's Flash text field folds those runs when it lays the string
        /// out, and a plain Unity label does not.</para>
        /// </summary>
        private static string CleanText(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            text = text.Replace("\r", string.Empty).Replace("\t", string.Empty);
            text = text.Replace("[br]", "\n");

            var builder = new System.Text.StringBuilder(text.Length);
            bool pendingSpace = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\n')
                {
                    // Trim the run before a break, then start the next line unindented.
                    while (builder.Length > 0 && builder[builder.Length - 1] == ' ') builder.Length--;
                    builder.Append('\n');
                    pendingSpace = false;
                    continue;
                }

                if (c == ' ')
                {
                    pendingSpace = true;
                    continue;
                }

                if (pendingSpace && builder.Length > 0 && builder[builder.Length - 1] != '\n')
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
                builder.Append(c);
            }

            return builder.ToString().Trim();
        }

        /// <summary>
        /// The Oracle's <c>@n</c> substitution (<c>GUI.as:1810-1818</c>, <c>Res.as:243-253</c>): for each
        /// of the five slots, replace <c>@i</c> with the name of the key that slot names.
        ///
        /// <para><b>Why the Flash span markup is not emitted.</b> The Oracle wraps the key name in
        /// <c>&lt;span class='yel'&gt;</c> / <c>'imp'</c> / <c>'r2'</c> depending on which of the three
        /// call sites it is in. Those classes are meaningless to a Unity label, and
        /// <c>PFE.UI.Common.PfeTextFormatter</c> already owns Flash-class → TMP conversion. Emitting
        /// spans here as well would give the project two formatters that can drift — the same reason
        /// this class refuses to do the <c>Res.txt</c> markup pass (see the class remarks).</para>
        /// </summary>
        /// <param name="text">The raw row or body text.</param>
        /// <param name="keys">The five key names, or <c>null</c>.</param>
        /// <param name="keyName">Resolves a key id to its caption — the Oracle's
        /// <c>Ctr.retKey</c>, which is <c>Res.txt("k", id)</c>.</param>
        public static string ApplyKeys(string text, string[] keys, Func<string, string> keyName = null)
        {
            if (string.IsNullOrEmpty(text) || keys == null) return text;

            for (int i = 1; i <= 5; i++)
            {
                string key = keys[i - 1];
                if (string.IsNullOrEmpty(key)) continue;

                string name = keyName != null ? keyName(key) : key;
                if (string.IsNullOrEmpty(name)) name = key;

                text = text.Replace("@" + i, name);
            }

            return text;
        }

        private static int CountEntries(Dictionary<string, Dictionary<string, Entry>> tables)
        {
            int total = 0;
            foreach (Dictionary<string, Entry> table in tables.Values) total += table.Count;
            return total;
        }

        // ── Lookup ────────────────────────────────────────────────────────────

        /// <summary>How many entries a table holds, by its full name. Zero when the table is absent.</summary>
        public int CountOf(string table)
        {
            if (table == null) return 0;
            return _active.TryGetValue(table, out Dictionary<string, Entry> entries) ? entries.Count : 0;
        }

        // ── Raw-table lookup — the `txt` table ────────────────────────────────

        /// <summary>
        /// The Oracle's <c>Res.d.txt.(@id == id)</c> — a lookup <b>by element name</b>, not through
        /// <c>Res.clob</c>.
        ///
        /// <para><b>Why this cannot go through <see cref="TableFor"/>.</b> <c>Res.clob</c> is the
        /// one-letter alias table, and it has no <c>txt</c> entry — because <c>txt</c> is never reached
        /// through an alias. <c>Res.txt("g", id)</c> and <c>Res.pipText(id)</c> go through the alias map;
        /// the two <b>script-facing</b> readers do not:
        /// <c>Res.messText</c> writes <c>d.txt.(@id == id)</c> (<c>Res.as:166</c>) and
        /// <c>GUI.dialText</c> writes <c>Res.d.txt.(@id == id)</c> (<c>GUI.as:1761</c>). So
        /// <c>beginMess</c>, <c>begDial</c>, <c>trWalk</c> — every message the campaign opens with — are
        /// rows of a table the alias map cannot name, and a port that only had
        /// <see cref="Text"/> could not resolve a single one of them.</para>
        /// </summary>
        /// <returns><c>true</c> when the table, the entry and the child all exist.</returns>
        public bool TryGetInTable(string table, string id, int razd, out string text)
        {
            text = null;
            if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(id)) return false;

            string child = ChildFor(razd);
            if (child == null) return false;

            if (!TryFind(_active, table, id, child, out Entry entry, out text))
            {
                // Res.as:168-171 / :76-86 — the default language is the fallback for a missing key.
                if (!TryFind(_fallback, table, id, child, out entry, out text)) return false;
            }

            if (entry.HasMatureVariant)
            {
                string[] parts = text.Split('|');
                if (parts.Length >= 2) text = parts[MatureContentFilter ? 1 : 0];
            }

            return true;
        }

        /// <summary>
        /// The Oracle's <c>Res.d.txt.(@id == id)</c> at a named child, or <c>null</c>. Unlike
        /// <see cref="Text"/> this does <b>not</b> synthesise a <c>*table_id</c> marker — the two
        /// script-facing readers both treat a miss as "no message" (<c>GUI.as:1766-1769</c>,
        /// <c>Res.as:172-173</c>), and a marker would be rendered as visible prose.</summary>
        public string TextInTable(string table, string id, int razd = 0) =>
            TryGetInTable(table, id, razd, out string text) ? text : null;

        /// <summary>
        /// An entry element's own attribute — <c>imp</c>, <c>m</c>, <c>s1</c>…<c>s5</c>, <c>st</c>.
        ///
        /// <para><c>Res.messText</c> gates on <c>@imp</c> (<c>Res.as:174-178</c>) and reads the entry's
        /// <c>s1</c>…<c>s5</c> for the <c>@n</c> substitution (<c>:243-253</c>); both are attributes of
        /// the element, not of a child, so neither <see cref="TryGetInTable"/> nor the child dictionary
        /// can reach them.</para>
        /// </summary>
        public string EntryAttribute(string table, string id, string attribute)
        {
            Entry entry = FindEntry(table, id);
            if (entry == null || string.IsNullOrEmpty(attribute)) return null;

            switch (attribute)
            {
                case "imp": return entry.Importance.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case "m": return entry.HasMatureVariant ? "1" : null;
            }

            if (attribute.Length == 2 && attribute[0] == 's' &&
                attribute[1] >= '1' && attribute[1] <= '5' && entry.Keys != null)
            {
                return entry.Keys[attribute[1] - '1'];
            }

            return null;
        }

        /// <summary>
        /// The number of spoken lines an entry has. <c>0</c> means the body is plain text rather than a
        /// list of <c>&lt;r&gt;</c> rows, which is the case <c>GUI.dialText</c> handles by rendering the
        /// <c>n</c> node directly (<c>GUI.as:1770-1784</c>).
        /// </summary>
        public int DialogueRowCount(string table, string id) => FindEntry(table, id)?.Rows?.Count ?? 0;

        /// <summary>
        /// Resolves row <paramref name="index"/> of an entry, with the <c>@n</c> key substitution already
        /// applied.
        ///
        /// <para><b>Index is the Oracle's <c>@n</c> and is zero-based, exactly as <c>GUI.dialText</c>
        /// treats it</b> — <c>begDial @n="0"</c> is "What happened? Where am I?" and <c>@n="2"</c> is
        /// "I`d better get going". A one-based reading here would shift the whole monologue.</para>
        /// </summary>
        /// <returns><c>false</c> when there is no such row — the Oracle's <c>return false</c> at
        /// <c>GUI.as:1778-1781</c>, which is what tells <c>Script.step</c> the monologue is over.</returns>
        public bool TryGetDialogueRow(string table, string id, int index, out DialogueRow row)
        {
            row = default;
            if (index < 0) return false;

            List<RawLine> rows = FindEntry(table, id)?.Rows;
            if (rows == null || index >= rows.Count) return false;

            RawLine raw = rows[index];
            row = new DialogueRow(
                speaker: raw.Speaker,
                text: ApplyKeys(raw.Text, raw.Keys, Key),
                mod: raw.Mod,
                push: raw.Push);
            return true;
        }

        /// <summary>
        /// Row <paramref name="index"/> exactly as the document holds it — the <c>@n</c> placeholders
        /// still in place, along with the speaker, the row's own key names and its <c>@m</c>.
        ///
        /// <para><b>Why a caller would want this instead of <see cref="TryGetDialogueRow"/>.</b> The
        /// resolved form substitutes the bare key <i>name</i> for <c>@n</c>; <c>Res.messText</c> wraps
        /// that name in <c>&lt;span class='yel'&gt;</c> (<c>Res.as:202-213</c>) and additionally picks a
        /// different wrapper for an <c>imp == 1</c> entry (<c>:215-233</c>). Reconstructing either from
        /// the resolved string is impossible, because the placeholder is gone by then. This is the same
        /// "raw form, resolve on read" split <see cref="ApplyKeys"/> already documents.</para>
        /// </summary>
        /// <returns><c>false</c> when there is no such row.</returns>
        public bool TryGetRawRow(string table, string id, int index, out RawLine line)
        {
            line = default;
            if (index < 0) return false;

            List<RawLine> rows = FindEntry(table, id)?.Rows;
            if (rows == null || index >= rows.Count) return false;

            line = rows[index];
            return true;
        }

        /// <summary>
        /// The whole entry as one string — the Oracle's <c>s = xml.n[0]</c> path for an entry with no
        /// <c>&lt;r&gt;</c> rows (<c>Res.as:237-240</c>), which is what <c>beginMess</c> and
        /// <c>trWalk</c> are.
        ///
        /// <para>Resolved against the <b>entry's</b> <c>s1</c>…<c>s5</c>, not a row's — the distinction
        /// <see cref="Entry.Keys"/> documents. Falls back to the rows joined by newlines when the entry
        /// has them, so a caller does not have to ask which shape it is holding.</para>
        /// </summary>
        public bool TryResolveBody(string table, string id, out string text)
        {
            text = null;
            Entry entry = FindEntry(table, id);
            if (entry == null) return false;

            if (entry.Rows is { Count: > 0 })
            {
                var builder = new System.Text.StringBuilder();
                for (int i = 0; i < entry.Rows.Count; i++)
                {
                    if (i > 0) builder.Append('\n');
                    builder.Append(ApplyKeys(entry.Rows[i].Text, entry.Rows[i].Keys, Key));
                }

                text = builder.ToString();
                return text.Length > 0;
            }

            if (!TryGetInTable(table, id, 0, out string body) || string.IsNullOrEmpty(body)) return false;

            text = ApplyKeys(CleanText(body), entry.Keys, Key);
            return text.Length > 0;
        }

        /// <summary>The entry's <c>@imp</c> — 0 when absent or unparsable.</summary>
        public int ImportanceOf(string table, string id) => FindEntry(table, id)?.Importance ?? 0;

        /// <summary>
        /// The <c>&lt;n&gt;</c> child's own children with this element name, in document order — the
        /// Oracle's <c>xml.n.t</c> / <c>xml.n.r</c> XMLList. Empty when the entry has no
        /// <c>&lt;n&gt;</c>, or the node has no child with that name.
        ///
        /// <para><b>The one caller is a note's title</b>, which is <c>xml.n.t[0]</c> with
        /// <c>xml.n.r[0]</c> as the fallback (<c>PipPageInfo.as:216-223</c>). Both are needed: the
        /// three seeded help notes author a <c>&lt;t&gt;</c>, and several terminal notes author only
        /// rows.</para>
        /// </summary>
        /// <param name="table">The table name — <c>"txt"</c> for the two script-facing readers.</param>
        /// <param name="childName">The element name under <c>&lt;n&gt;</c> — <c>"t"</c> or <c>"r"</c>.</param>
        public IReadOnlyList<string> NChildTexts(string table, string id, string childName)
        {
            Entry entry = FindEntry(table, id);
            if (entry?.NChildren == null || string.IsNullOrEmpty(childName)) return Array.Empty<string>();

            return entry.NChildren.TryGetValue(childName, out List<string> texts)
                ? (IReadOnlyList<string>)texts
                : Array.Empty<string>();
        }

        /// <summary>
        /// Whether an entry with this id exists in the table, in either document.
        ///
        /// <para><b>A question distinct from "does it have a child".</b> <c>Res.messText</c> asks it
        /// first and returns the empty string for a miss (<c>Res.as:169-172</c>) — before it ever looks
        /// at <c>@imp</c> or at <c>n</c>. A caller that instead tested the <c>n</c> child would treat
        /// "entry present but bodyless" and "entry absent" as the same answer, and they are not: the
        /// first still has an <c>@imp</c> and an <c>&lt;info&gt;</c>.</para>
        /// </summary>
        public bool HasEntry(string table, string id)
        {
            if (FindEntry(table, id) != null) return true;

            // Res.as:165-171 — the default language is the fallback for a missing key.
            if (ReferenceEquals(_active, _fallback)) return false;
            return _fallback.TryGetValue(table ?? "", out Dictionary<string, Entry> entries)
                && entries.ContainsKey(id ?? "");
        }

        private Entry FindEntry(string table, string id)
        {
            if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(id)) return null;

            if (_active.TryGetValue(table, out Dictionary<string, Entry> entries) &&
                entries.TryGetValue(id, out Entry entry))
            {
                return entry;
            }

            return _fallback.TryGetValue(table, out entries) && entries.TryGetValue(id, out entry)
                ? entry
                : null;
        }

        /// <summary>
        /// The Oracle's lookup without its missing-key marker: <c>true</c> when the alias resolves, the
        /// entry exists, and it has the child <paramref name="razd"/> selects.
        /// </summary>
        /// <param name="alias">A <c>Res.clob</c> key — <c>"p"</c>, <c>"e"</c>, <c>"g"</c>…</param>
        /// <param name="id">The entry's <c>id</c> attribute.</param>
        /// <param name="razd">The child index: 0 = <c>n</c>, 1 = <c>info</c>, 2 = <c>mess</c>, 3 = <c>help</c>.</param>
        /// <param name="text">The resolved text, with the mature-variant split already applied.</param>
        public bool TryGet(string alias, string id, int razd, out string text)
        {
            text = null;
            if (string.IsNullOrEmpty(id)) return false;

            string table = TableFor(alias);
            string child = ChildFor(razd);
            if (table == null || child == null) return false;

            if (!TryFind(_active, table, id, child, out Entry entry, out text))
            {
                // Res.as:76-86 — the default language is the fallback for a missing key.
                if (!TryFind(_fallback, table, id, child, out entry, out text)) return false;
            }

            if (entry.HasMatureVariant)
            {
                // Res.as:100-107 — a `|`-separated pair, first part for the filtered build.
                string[] parts = text.Split('|');
                if (parts.Length >= 2) text = parts[MatureContentFilter ? 1 : 0];
            }

            return true;
        }

        private static bool TryFind(
            Dictionary<string, Dictionary<string, Entry>> tables,
            string table, string id, string child, out Entry entry, out string text)
        {
            entry = null;
            text = null;

            if (!tables.TryGetValue(table, out Dictionary<string, Entry> entries)) return false;
            if (!entries.TryGetValue(id, out entry)) return false;
            if (!entry.Children.TryGetValue(child, out text)) return false;

            return true;
        }

        /// <summary>
        /// The Oracle's <c>Res.txt</c> (<c>Res.as:55-137</c>) at the point where it returns a string.
        ///
        /// <para><b>The missing-key marker is reproduced, not replaced.</b> <c>Res.as:93-97</c>
        /// returns <c>"*" + clob[tip] + "_" + id</c> for a missing entry at <c>razd == 0</c>, and
        /// <c>""</c> for a missing child at any other <c>razd</c>. That is how the shipped game shows
        /// a key it cannot resolve — a visible <c>*pip_main4</c> on screen — and reproducing it means a
        /// caller can tell "no table" from "no such key" from "the key exists but has no description",
        /// which a silent <c>""</c> would erase. Callers that want prose instead should ask
        /// <see cref="TryGet"/>.</para>
        /// </summary>
        /// <param name="alias">A <c>Res.clob</c> key.</param>
        /// <param name="id">The entry id.</param>
        /// <param name="razd">The child index; see <see cref="TryGet"/>.</param>
        public string Text(string alias, string id, int razd = 0)
        {
            if (string.IsNullOrEmpty(id)) return string.Empty;   // Res.as:64-67

            if (TryGet(alias, id, razd, out string text)) return text;

            // Res.as:89-92 — the `obj` table never reports a miss, because an unnamed object is
            // legal and common; every other table says so loudly.
            if (alias == "o") return string.Empty;

            if (razd != 0) return string.Empty;

            string table = TableFor(alias);
            return table == null ? string.Empty : "*" + table + "_" + id;
        }

        // ── The Oracle's named helpers (Res.as:139-147) ───────────────────────

        /// <summary><c>Res.pipText(id)</c> — the PipBuck's own table.</summary>
        public string Pip(string id) => Text("p", id);

        /// <summary><c>Res.guiText(id)</c> — the interface's general table.</summary>
        public string Gui(string id) => Text("g", id);

        /// <summary><c>Res.txt("e", id, razd)</c> — effect / skill / perk names and descriptions.</summary>
        public string Eff(string id, int razd = 0) => Text("e", id, razd);

        /// <summary><c>Res.txt("u", id)</c> — character names.</summary>
        public string Unit(string id) => Text("u", id);

        /// <summary><c>Res.txt("i", id)</c> — item names.</summary>
        public string Item(string id) => Text("i", id);

        /// <summary><c>Res.txt("w", id)</c> — weapon names.</summary>
        public string Weapon(string id) => Text("w", id);

        /// <summary><c>Res.txt("a", id)</c> — armour names.</summary>
        public string Armor(string id) => Text("a", id);

        /// <summary><c>Res.txt("f", id)</c> — interaction messages.</summary>
        public string Info(string id) => Text("f", id);

        /// <summary><c>Res.txt("m", id)</c> — location names.</summary>
        public string Map(string id) => Text("m", id);

        /// <summary><c>Res.txt("k", id)</c> — key names, the table <c>Ctr.retKey</c> resolves against.</summary>
        public string Key(string id) => Text("k", id);
    }
}
