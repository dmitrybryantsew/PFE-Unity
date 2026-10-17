using System;
using System.Text;

namespace PFE.UI.PipBuck
{
    /// <summary>
    /// The port of <c>Res.messText</c> (<c>Res.as:149-264</c>) — the long-form message body the PipBuck
    /// shows for a note, a help entry or an opening briefing.
    ///
    /// <para><b>Why this is its own class and not a method on <see cref="PipTextTable"/>.</b>
    /// <c>PipTextTable</c> deliberately stops short of the markup pass: its remarks say that resolving
    /// <c>@n</c> to a <i>bare</i> key name and leaving Flash-class → Unity-colour conversion to
    /// <c>PfeTextFormatter</c> is the project's one-formatter policy, and <c>TryResolveBody</c>
    /// implements exactly that. <c>Res.messText</c> is the other case — it emits
    /// <c>&lt;span class='yel'&gt;</c>, <c>'dark'</c>, <c>'light'</c> and <c>'r2'</c> itself, and which
    /// one it picks depends on the entry's <c>@imp</c> and each row's <c>@p</c>. That is page-specific
    /// behaviour, not a table lookup, so it lives here and <c>TryResolveBody</c> is left alone for the
    /// callers that already rely on it.</para>
    ///
    /// <para><b>Three branches, all live in the shipped document.</b></para>
    /// <list type="number">
    /// <item><b><c>@imp == 1</c> is a dialogue.</b> Rows are wrapped by speaker: an un-attributed row in
    /// <c>dark</c>, a row whose <c>@p</c> starts <c>lp</c> (the player) in <c>light</c> and prefixed
    /// <c>" - "</c>, anything else plain and prefixed <c>" - "</c>. 96 of the 584 <c>txt</c> entries
    /// carry <c>imp="1"</c> and 1801 of the 1930 rows carry a <c>@p</c>, so this is the common case,
    /// not a corner.</item>
    /// <item><b><c>@imp</c> 2 or 3 is prose.</b> Rows are joined with <c>&lt;br&gt;</c> and the
    /// substituted key names are yellow. The three seeded help notes (<c>helpControl</c>,
    /// <c>helpGl1</c>, <c>helpGl2</c>) are all <c>imp="3"</c> and are what the Notes tab shows on a
    /// fresh campaign.</item>
    /// <item><b>No <c>&lt;r&gt;</c> rows at all.</b> The whole <c>&lt;n&gt;</c> node is the body
    /// (<c>Res.as:237-242</c>).</item>
    /// </list>
    ///
    /// <para><b>What is deliberately not reproduced.</b> The Oracle wraps the entire call in
    /// <c>try/catch</c> and returns <c>"err: " + id</c> on a throw (<c>Res.as:259-262</c>). Nothing in
    /// this port throws — every lookup answers "absent" — so there is no path to that string, and
    /// inventing one would put a message on screen that the port never actually produces. The
    /// <c>@1…@5</c> substitution is done here (not by <c>PfeTextFormatter</c>) because the Oracle does
    /// it here and wraps the result in a class of its own choosing.</para>
    /// </summary>
    public static class PipMessageBody
    {
        /// <summary>
        /// The port of <c>Res.messText(id, v, imp)</c>.
        /// </summary>
        /// <param name="text">The text table.</param>
        /// <param name="id">The <c>txt</c> entry id.</param>
        /// <param name="razd">AS3's <c>v</c>: <c>0</c> builds the body, <c>1</c> returns the
        /// <c>&lt;info&gt;</c> child verbatim (<c>Res.as:178-181</c>).</param>
        /// <param name="important">AS3's <c>imp</c>, whose <b>default in the Oracle is <c>true</c></b>
        /// (<c>Res.as:149</c>). When false, an entry whose <c>@imp</c> is 0 or absent yields the empty
        /// string instead of its body — the "show this only when it matters" filter. The Notes tab
        /// passes <c>false</c>, which is why a note with no <c>@imp</c> would render blank.</param>
        /// <param name="playerName">AS3 <c>World.w.pers.persName</c> — substituted for every
        /// <c>&amp;lp</c>.</param>
        /// <param name="keyName">AS3 <c>World.w.ctr.retKey</c>. Defaults to
        /// <c>Res.txt("k", keyId)</c>, which is the port's documented stand-in: the real <c>retKey</c>
        /// also maps a key id to a physical key code and renders it as <c>[Name]</c>, and the port has
        /// no key-name table yet. Pass a resolver once it exists.</param>
        public static string Build(
            PipTextTable text,
            string id,
            int razd = 0,
            bool important = true,
            string playerName = "",
            Func<string, string> keyName = null)
        {
            if (text == null || string.IsNullOrEmpty(id)) return "";

            // `if(!imp && xml.@imp <= 0) return "";` — note `<= 0`, so an absent @imp reads as 0.
            if (!important && text.ImportanceOf("txt", id) <= 0) return "";

            // `xml.length() == 0` → the entry does not exist. The Oracle returns "" here, not the
            // missing-key marker — this reader treats a miss as "no message" (Res.as:169-172).
            if (!text.HasEntry("txt", id)) return "";

            int importance = text.ImportanceOf("txt", id);

            if (razd == 1)
            {
                return text.TextInTable("txt", id, 1) ?? "";
            }

            string body;
            int rowCount = text.DialogueRowCount("txt", id);

            if (rowCount > 0)
            {
                var builder = new StringBuilder();
                for (int i = 0; i < rowCount; i++)
                {
                    if (!text.TryGetRawRow("txt", id, i, out PipTextTable.RawLine line)) continue;

                    string rowText = ApplyMatureVariant(line);
                    rowText = SubstituteKeys(rowText, line.Keys, "<span class='yel'>", "</span>", keyName, text);
                    rowText = StripControl(rowText);

                    if (importance == 1)
                    {
                        if (string.IsNullOrEmpty(line.Speaker))
                        {
                            builder.Append("<span class='dark'>").Append(rowText).Append("</span>");
                        }
                        else if (line.Speaker.StartsWith("lp", StringComparison.Ordinal))
                        {
                            builder.Append("<span class='light'>").Append(" - ").Append(rowText).Append("</span>");
                        }
                        else
                        {
                            builder.Append(" - ").Append(rowText);
                        }
                    }
                    else
                    {
                        builder.Append(rowText);
                    }

                    builder.Append("<br>");
                }

                body = builder.ToString();
            }
            else
            {
                // `s = xml.n[0]` — the whole `n` node, raw. Note the Oracle does NOT strip control
                // characters on this path: the `replace(/[\b\r\t]/g,"")` above is inside the rows loop.
                body = text.TextInTable("txt", id, 0) ?? "";
            }

            body = body.Replace("&lp", playerName ?? "");

            // `s.replace(/\[br\]/g,"<br>")` — the literal token, distinct from the `<br>` the row loop
            // already emitted.
            body = body.Replace("[br]", "<br>");

            // The entry-level pass. Gated on `xml.@s1.length()` alone and then looping all five, so an
            // entry that authors `s2` without `s1` gets no substitution at all — the Oracle's own
            // asymmetry, reproduced rather than tidied.
            if (text.EntryAttribute("txt", id, "s1") != null)
            {
                string[] entryKeys = new string[5];
                for (int i = 1; i <= 5; i++) entryKeys[i - 1] = text.EntryAttribute("txt", id, "s" + i);
                body = SubstituteKeys(body, entryKeys, "<span class='r2'>", "</span>", keyName, text);
            }

            return body;
        }

        /// <summary>
        /// The row's <c>@m</c> split (<c>Res.as:187-201</c>) — the text is a <c>|</c>-separated pair and
        /// the active half depends on the mature-content filter.
        ///
        /// <para>Two guards in the Oracle that matter: the split only applies when the attribute is
        /// present, and the branch is only taken when the split actually produced more than one part
        /// (<c>if(sar.length &gt; 1)</c>), so a row marked <c>m='1'</c> with no <c>|</c> in it survives
        /// intact rather than being truncated to its first part.</para>
        /// </summary>
        private static string ApplyMatureVariant(PipTextTable.RawLine line)
        {
            if (!line.Mature) return line.Text;

            string[] parts = (line.Text ?? "").Split('|');
            if (parts.Length <= 1) return line.Text;

            return PipTextTable.MatureContentFilter ? parts[1] : parts[0];
        }

        /// <summary>
        /// <c>@1</c>…<c>@5</c> → the wrapped key name (<c>Res.as:202-213</c>, <c>:246-257</c>).
        ///
        /// <para>Loops all five slots and replaces only those the row authored — so an un-authored slot
        /// leaves a literal <c>@3</c> in the text, which is exactly what the Oracle does and what a
        /// reader sees when a row is written for a key that was never bound.</para>
        /// </summary>
        private static string SubstituteKeys(
            string text,
            string[] keys,
            string open,
            string close,
            Func<string, string> keyName,
            PipTextTable table)
        {
            if (string.IsNullOrEmpty(text) || keys == null) return text;

            for (int i = 1; i <= 5; i++)
            {
                string key = keys[i - 1];
                if (string.IsNullOrEmpty(key)) continue;

                string name = keyName != null ? keyName(key) : null;
                if (string.IsNullOrEmpty(name)) name = table.Key(key);

                text = text.Replace("@" + i, open + name + close);
            }

            return text;
        }

        /// <summary><c>s.replace(/[\b\r\t]/g,"")</c> (<c>Res.as:214</c>) — backspace, CR and tab only;
        /// the newline survives, because the page's layout depends on it.</summary>
        private static string StripControl(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return text.Replace("\b", "").Replace("\r", "").Replace("\t", "");
        }
    }
}
