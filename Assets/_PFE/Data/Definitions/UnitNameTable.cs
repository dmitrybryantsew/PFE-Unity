using System;
using System.Collections.Generic;
using UnityEngine;

namespace PFE.Data.Definitions
{
    /// <summary>
    /// The per-species name lists a starred enemy draws its name from — AS3 <c>Res.namesArr(id)</c>
    /// (<c>Res.as:313-335</c>) plus the draw-without-replacement that <c>Unit.getName()</c>
    /// (<c>Unit.as:1477-1497</c>) performs on it.
    ///
    /// <para><b>Where the data is.</b> The lists live in the localisation document, not in
    /// <c>AllData.as</c>: <c>&lt;names&gt;&lt;name id='raider'&gt;&lt;r&gt;Pooptooth&lt;/r&gt;…</c>. That
    /// is the same 800 KB file the PipBuck reads (<c>Resources/Localisation/text_en.xml</c>), which is
    /// why this type parses the document itself rather than duplicating the table into an asset — a
    /// copy would be a second source of truth for ~600 strings that the localisation pass already
    /// owns.</para>
    ///
    /// <para><b>Names are consumed, not sampled.</b> The oracle <i>splices</i> the chosen name out of
    /// the array and caches the shortened array on the save (<c>World.w.game.names[id]</c>,
    /// <c>:1483-1495</c>), so two raiders in one playthrough never share a name. That is a session
    /// property, not a per-unit one, which is why <see cref="Shared"/> is a static instance with a
    /// <see cref="Reset"/> — the same lifetime the oracle ties to the game object, and the same shape
    /// <c>PfeTextTableProvider</c> already uses for the document.</para>
    ///
    /// <para><b>An exhausted list is not an error.</b> <c>getName()</c> returns <c>""</c> once the array
    /// is empty, and <c>setHero</c> then keeps the unit's default name
    /// (<c>if(_loc2_ != null &amp;&amp; _loc2_ != "") nazv = _loc2_</c>, <c>:1640-1645</c>). A nineteenth
    /// raider in a land is simply "Raider".</para>
    /// </summary>
    public sealed class UnitNameTable
    {
        /// <summary>
        /// The localisation document, as a Unity resource path. The same file
        /// <c>PFE.UI.Common.PfeTextTableProvider.TextTableResourcePath</c> names — repeated rather than
        /// referenced because that type lives in the UI assembly and this one must not depend on it.
        /// </summary>
        public const string TextTableResourcePath = "Localisation/text_en";

        /// <summary>The element that opens the block — <c>&lt;names&gt;</c>.</summary>
        private const string NamesOpen = "<names";

        /// <summary>The element that closes it.</summary>
        private const string NamesClose = "</names>";

        /// <summary>Remaining names per list key, in document order. Shortened as names are taken.</summary>
        private readonly Dictionary<string, List<string>> _lists;

        /// <summary>
        /// Every key the document authored a list for, <b>including ones that have been used up</b>.
        ///
        /// <para>Separate from <see cref="_lists"/> on purpose: <see cref="HasList"/> answers a
        /// <i>data</i> question ("can this species be a hero at all"), and deriving it from the live
        /// lists would make a species stop being hero-capable the moment its eighteenth name was drawn —
        /// which is a different question and a very confusing bug.</para>
        /// </summary>
        private readonly HashSet<string> _keys;

        /// <summary>An empty table — every lookup misses. Never null, so callers need no null checks.</summary>
        public static UnitNameTable Empty { get; } = new UnitNameTable(
            new Dictionary<string, List<string>>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));

        private UnitNameTable(Dictionary<string, List<string>> lists, HashSet<string> keys)
        {
            _lists = lists;
            _keys = keys;
        }

        private static UnitNameTable _shared;

        /// <summary>
        /// The session's table, parsed on first use from the localisation document.
        ///
        /// <para>A missing document yields <see cref="Empty"/> and one warning, not an exception: every
        /// caller has a documented fallback (the unit keeps its default name), and failing here would
        /// take a spawn down over a cosmetic string.</para>
        /// </summary>
        public static UnitNameTable Shared
        {
            get
            {
                if (_shared != null) return _shared;

                TextAsset asset = Resources.Load<TextAsset>(TextTableResourcePath);
                if (asset == null)
                {
                    Debug.LogWarning(
                        "[UnitNameTable] No localisation document at Resources/" + TextTableResourcePath +
                        ".xml — starred enemies keep their default species name.");
                    _shared = Empty;
                    return _shared;
                }

                _shared = Parse(asset.text);
                return _shared;
            }
        }

        /// <summary>
        /// Drop the session's table, so the next <see cref="Shared"/> re-parses and every name is
        /// available again. For a new game and for a test that swaps the document.
        /// </summary>
        public static void Reset() => _shared = null;

        /// <summary>
        /// True when the document authored a list for <paramref name="key"/> — <b>whether or not it has
        /// been used up</b>. This is the port's answer to AS3 <c>Unit.mHero</c>, which is a data flag on
        /// the unit row rather than a property of the name pool.
        /// </summary>
        public bool HasList(string key) => !string.IsNullOrEmpty(key) && _keys.Contains(key);

        /// <summary>
        /// Take one name for <paramref name="key"/>, removing it from the list — AS3's
        /// <c>Math.floor(Math.random() * arr.length)</c> followed by <c>arr.splice(idx, 1)</c>.
        ///
        /// <para><b>The draw is the caller's, deliberately.</b> <c>getName()</c> calls
        /// <c>Math.random()</c>, i.e. the oracle's one global stream, and the spawn path is the one place
        /// in the port that owns a seeded equivalent. Taking a <c>[0,1)</c> value instead of an
        /// <c>IRngService</c> keeps this type engine-free and makes the "which index does this draw
        /// pick" question testable without a generator.</para>
        /// </summary>
        /// <returns><c>false</c> when there is no list, or it is exhausted — keep the default name.</returns>
        public bool TryTake(string key, double draw01, out string name)
        {
            name = null;

            if (string.IsNullOrEmpty(key) || !_lists.TryGetValue(key, out List<string> list) || list.Count == 0)
            {
                return false;
            }

            int index = (int)Math.Floor(draw01 * list.Count);
            if (index < 0) index = 0;
            if (index >= list.Count) index = list.Count - 1;

            name = list[index];
            list.RemoveAt(index);
            return !string.IsNullOrEmpty(name);
        }

        /// <summary>
        /// Parse the <c>&lt;names&gt;</c> block out of a localisation document.
        ///
        /// <para><b>Why a scan and not an <c>XmlDocument</c>.</b> The block is a flat two-level list with
        /// no attributes beyond <c>id</c>, and the document around it is 13 000 lines the caller has
        /// already paid to load. Loading it a second time into a DOM to read six hundred
        /// <c>&lt;r&gt;</c> elements would cost more than the whole rest of the spawn path.</para>
        ///
        /// <para>Both quote styles are accepted for <c>id</c>: the shipped document uses
        /// <c>id='raider'</c>, and a re-export from the original tooling is as likely to emit
        /// <c>id="raider"</c>. Getting that wrong yields an empty table that reads exactly like "no
        /// names in this build".</para>
        /// </summary>
        public static UnitNameTable Parse(string xml)
        {
            var lists = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var keys = new HashSet<string>(StringComparer.Ordinal);

            if (string.IsNullOrEmpty(xml))
            {
                return new UnitNameTable(lists, keys);
            }

            int blockStart = xml.IndexOf(NamesOpen, StringComparison.Ordinal);
            if (blockStart < 0)
            {
                return new UnitNameTable(lists, keys);
            }

            int blockEnd = xml.IndexOf(NamesClose, blockStart, StringComparison.Ordinal);
            if (blockEnd < 0)
            {
                blockEnd = xml.Length;
            }

            int cursor = blockStart;
            while (true)
            {
                int nameStart = xml.IndexOf("<name ", cursor, StringComparison.Ordinal);
                if (nameStart < 0 || nameStart >= blockEnd) break;

                int openEnd = xml.IndexOf('>', nameStart);
                if (openEnd < 0 || openEnd >= blockEnd) break;

                string attributes = xml.Substring(nameStart + 6, openEnd - nameStart - 6);
                string id = ReadAttribute(attributes, "id");

                int close = xml.IndexOf("</name>", openEnd, StringComparison.Ordinal);
                if (close < 0 || close > blockEnd) break;

                if (!string.IsNullOrEmpty(id))
                {
                    List<string> names = ReadNames(xml, openEnd + 1, close);
                    if (names.Count > 0)
                    {
                        lists[id] = names;
                        keys.Add(id);
                    }
                }

                cursor = close + "</name>".Length;
            }

            return new UnitNameTable(lists, keys);
        }

        /// <summary>Every <c>&lt;r&gt;</c> value between <paramref name="start"/> and <paramref name="end"/>.</summary>
        private static List<string> ReadNames(string xml, int start, int end)
        {
            var names = new List<string>();

            int cursor = start;
            while (cursor < end)
            {
                int rStart = xml.IndexOf("<r>", cursor, StringComparison.Ordinal);
                if (rStart < 0 || rStart >= end) break;

                int textStart = rStart + 3;
                int rEnd = xml.IndexOf("</r>", textStart, StringComparison.Ordinal);
                if (rEnd < 0 || rEnd > end) break;

                string name = xml.Substring(textStart, rEnd - textStart).Trim();
                if (name.Length > 0)
                {
                    names.Add(DecodeEntities(name));
                }

                cursor = rEnd + 4;
            }

            return names;
        }

        /// <summary>The value of <paramref name="attribute"/> in an element's attribute text, or <c>null</c>.</summary>
        private static string ReadAttribute(string attributes, string attribute)
        {
            int at = attributes.IndexOf(attribute + "=", StringComparison.Ordinal);
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
        /// The five predefined XML entities, decoded. Names are player-facing text and the original
        /// document does use <c>&amp;amp;</c>-style escapes in other sections; a raw <c>&amp;</c> reaching
        /// a HUD label renders as-is, but an escaped one would show its escape.
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
