using System;
using System.Collections.Generic;


namespace PFE.UI.PipBuck
{
    /// <summary>One row of the note list — the port of <c>setStatItem</c>'s <c>page2 == 4</c> arm
    /// (<c>PipPageInfo.as:327-335</c>).</summary>
    public sealed class PipNoteRow
    {
        /// <summary>The note's <c>txt</c> entry id — the row's hidden <c>id</c> field, and what the
        /// detail pane resolves against (<c>PipPageInfo.as:427</c>).</summary>
        public string NoteId = "";

        /// <summary>The full title, with <c>&amp;lp</c> already replaced by the player's name. This is
        /// what the row's <c>kol</c> field holds (<c>PipPageInfo.as:331</c>) — a hidden full copy, so
        /// the truncated caption can be widened later without re-resolving anything.</summary>
        public string Title = "";

        /// <summary>The drawn caption — <see cref="Title"/> after the Oracle's truncation
        /// (<c>PipPageInfo.as:330</c>).</summary>
        public string Caption = "";

        /// <summary>AS3 <c>ico</c> — the entry's <c>@imp</c>, 1..3. Only <c>&gt; 0</c> reaches the
        /// list.</summary>
        public int Importance;

        /// <summary>The <c>mq</c> marker's frame — <c>ico + 1</c> (<c>PipPageInfo.as:334</c>). Frame 1
        /// is the main-quest marker, which is why a note never uses it.</summary>
        public int MarkerFrame;

        /// <summary>The caption column's x — <c>param1.nazv.x = 32</c> (<c>PipPageInfo.as:329</c>).</summary>
        public float NameX = PipInfoFrame.NoteNameX;
    }

    /// <summary>
    /// Sub-tab 4 — the note list. The port of <c>PipPageInfo</c>'s <c>page2 == 4</c> build
    /// (<c>:206-242</c>), of <c>setStatItem</c>'s note arm (<c>:327-335</c>) and of the note body in
    /// <c>statInfo</c> (<c>:424-432</c>).
    ///
    /// <para><b>The ordering is two passes and reads backwards from the data.</b> Notes marked
    /// <c>@imp="3"</c> are collected separately and go to the <b>front, in the order the game added
    /// them</b>; every other note follows, <b>reversed</b>. So the newest ordinary note is at the top
    /// of the second block and the oldest is directly under the important ones — a list that looks
    /// sorted by importance until you notice the second half is upside down.</para>
    ///
    /// <para><b>The note list itself is not derived from the text table.</b> <c>game.notes</c> is an
    /// ordered array on the campaign (<c>Game.as:666-673</c>) that only ever gains entries through
    /// <c>addNote</c>, guarded by a <c>note_&lt;id&gt;</c> trigger so a note cannot be added twice. A
    /// note that exists in the document but was never added is invisible here, which is the point:
    /// notes are discoveries, not a manual. The three seeded ones are <c>helpControl</c>,
    /// <c>helpGl1</c>, <c>helpGl2</c> (<c>Game.as:301-303</c>).</para>
    /// </summary>
    public static class PipNoteJournal
    {
        /// <summary>The <c>@imp</c> value that floats a note to the front
        /// (<c>PipPageInfo.as:230</c>).</summary>
        public const int ImportantImportance = 3;

        /// <summary>
        /// The note list — the port of <c>PipPageInfo.as:206-242</c>.
        ///
        /// <para>A note is dropped when its <c>txt</c> entry is missing <b>or</b> its <c>@imp</c> is not
        /// greater than zero (<c>:211-213</c>). Both are silent drops, so the list can legitimately be
        /// shorter than <paramref name="noteIds"/> — and on a fresh campaign it is exactly the three
        /// seeded help notes, all <c>imp="3"</c>.</para>
        /// </summary>
        /// <param name="noteIds">AS3 <c>game.notes</c>, in the order the game added them.</param>
        /// <param name="text">The text table.</param>
        /// <param name="playerName">AS3 <c>World.w.pers.persName</c>, substituted for <c>&amp;lp</c> in
        /// each title.</param>
        public static List<PipNoteRow> BuildList(
            IEnumerable<string> noteIds,
            PipTextTable text,
            string playerName = "")
        {
            var important = new List<PipNoteRow>();
            var ordinary = new List<PipNoteRow>();

            if (noteIds == null) return important;

            foreach (string noteId in noteIds)
            {
                if (string.IsNullOrEmpty(noteId)) continue;
                if (!text.HasEntry("txt", noteId)) continue;

                int importance = text.ImportanceOf("txt", noteId);
                if (importance <= 0) continue;

                string title = TitleFor(noteId, text, playerName);

                var row = new PipNoteRow
                {
                    NoteId = noteId,
                    Title = title,
                    Caption = CaptionFor(title),
                    Importance = importance,
                    MarkerFrame = PipInfoFrame.MarkerNoteFrame(importance),
                };

                // `if(nico == 3) doparr.push(n); else arr.push(n);`
                if (importance == ImportantImportance) important.Add(row);
                else ordinary.Add(row);
            }

            // `arr.reverse(); arr = doparr.concat(arr);`
            ordinary.Reverse();
            important.AddRange(ordinary);
            return important;
        }

        /// <summary>
        /// A note's title — <c>PipPageInfo.as:216-223</c>: the entry's <c>&lt;n&gt;</c> node's first
        /// <c>&lt;t&gt;</c> child, else its first <c>&lt;r&gt;</c> row, with <c>&amp;lp</c> replaced.
        ///
        /// <para><b>The fallback is not a nicety.</b> The three seeded help notes carry a
        /// <c>&lt;t&gt;</c> (<c>ADVANCED CONTROLS</c>, <c>GLOSSARY</c>, <c>MINISTRIES</c>) and several
        /// terminal notes carry none — their first body line becomes the title.</para>
        /// </summary>
        public static string TitleFor(string noteId, PipTextTable text, string playerName = "")
        {
            if (text == null || string.IsNullOrEmpty(noteId)) return "";

            string title = null;

            IReadOnlyList<string> titles = text.NChildTexts("txt", noteId, "t");
            if (titles.Count > 0) title = titles[0];

            if (string.IsNullOrEmpty(title))
            {
                // `xml.n.r[0]` — the first row's own text, with `@n` left in place: a title is not a
                // place the Oracle substitutes key names.
                if (text.TryGetRawRow("txt", noteId, 0, out PipTextTable.RawLine line)) title = line.Text;
            }

            if (title == null) return "";

            return title.Replace("&lp", playerName ?? "");
        }

        /// <summary>
        /// The row caption's truncation — <c>PipPageInfo.as:330</c>, reproduced exactly:
        /// <c>nazv.substr(nazv.charAt(0) == " " ? 3 : 0, 60)</c>.
        ///
        /// <para><b>Two quirks, both deliberate.</b> The cap is 60 <i>characters</i>, and a title whose
        /// first character is a space is trimmed by <b>three</b> rather than by one — the Oracle's note
        /// titles are authored with a leading indent inside <c>&lt;n&gt;</c> and the extra two characters
        /// are eaten with it. A reader who "fixes" the 3 to a 1 shifts every indented title by two
        /// characters, which is invisible until a long one is truncated. <b>In the port</b> the parser
        /// (<c>PipTextTable.CleanText</c>) already trims, so parser-fed titles arrive without the indent
        /// and this rule only fires on titles that reach it by another route — the port keeps the rule
        /// because the Oracle's <c>substr</c> line has it, and the fixture pins it on synthetic input.</para>
        /// </summary>
        public static string CaptionFor(string title)
        {
            if (string.IsNullOrEmpty(title)) return "";

            int start = title[0] == ' ' ? PipInfoFrame.NoteTitleSpaceTrim : 0;
            if (start >= title.Length) return "";

            int length = Math.Min(PipInfoFrame.NoteTitleMaxChars, title.Length - start);
            return title.Substring(start, length);
        }

        /// <summary>
        /// The detail pane's body — <c>PipPageInfo.as:424-432</c>.
        ///
        /// <para><b>Three passes, in this order.</b> <c>Res.messText(id, 0, false)</c> builds the body
        /// (the <c>false</c> is what makes an <c>@imp</c>-less note render blank rather than as its
        /// text); <c>&amp;lp</c> becomes the player's name; and every <c>[</c>/<c>]</c> becomes a
        /// <c>yel</c> span. The bracket pass runs <b>after</b> <c>messText</c>, which matters because
        /// <c>messText</c> is what turns the literal token <c>[br]</c> into a line break — a body
        /// written as <c>[br]</c> therefore gets its break, and a body written as <c>[Some Term]</c>
        /// gets yellow brackets and no break.</para>
        ///
        /// <para>The note's <c>Res.messText</c> already substitutes <c>&amp;lp</c> internally, so the
        /// replacement here is a second, idempotent pass — the Oracle does both and the port keeps both
        /// rather than reasoning about which one wins.</para>
        /// </summary>
        public static string BuildBody(string noteId, PipTextTable text, string playerName = "")
        {
            if (text == null || string.IsNullOrEmpty(noteId)) return "";

            string body = PipMessageBody.Build(text, noteId, razd: 0, important: false, playerName: playerName);

            body = body.Replace("&lp", playerName ?? "");
            body = body.Replace("[", "<span class='yel'>");
            body = body.Replace("]", "</span>");
            return body;
        }
    }
}
