using System;
using System.Collections.Generic;
using UnityEngine;

namespace PFE.UI.HUD
{
    /// <summary>
    /// The port of AS3 <c>GUI.infoText</c> (<c>inter/GUI.as:1553-1577</c>) and the timer that drains it
    /// (<c>:1944-1965</c>, <c>remStr</c> at <c>:1640-1650</c>) — the scrolling message list in the
    /// corner, which is what tells the player "Found: Screwdriver".
    ///
    /// <para><b>The four numbers, and none of them are invented:</b> six lines maximum
    /// (<c>kolStr &gt; 6 → remStr</c>), a 150-tick hold on every add (<c>t_info = 150</c>), a 60-tick
    /// re-arm once the timer runs out with lines still showing (<c>:1961-1963</c>), and a repeat
    /// window: at <c>t_info == 100</c> the previous line is forgotten (<c>:1957-1959</c>), so the same
    /// message can be added again after 50 ticks but is de-duplicated before that.</para>
    ///
    /// <para><b>Why the de-dupe exists at all.</b> Picking a lock, or holding the take key over a pile,
    /// re-runs <c>infoText("take", …)</c> many times per second. Without the
    /// <c>_loc5_ != prevInfoText</c> guard the list would scroll six identical lines and drain the real
    /// ones. The guard is on the <i>formatted</i> string, not the key, because two different items share
    /// the <c>take</c> template.</para>
    ///
    /// <para><b>Frames, not seconds.</b> The oracle counts frames and the port's HUD counts Update
    /// frames — the same convention <c>ScriptToastView.ToastFrames</c> already uses for its 150.
    /// <b>Note the direction: at a 60 Hz display the hold is <i>half</i> the oracle's</b> — the AS3
    /// frame loop runs at 30 fps (<c>SimClock.DefaultTicksPerSecond</c>, one tick = one
    /// <c>ENTER_FRAME</c>), so its 150 frames are 5 s where the HUD's 150 frames are 2.5 s. An earlier
    /// draft of this comment claimed "twice as long", which was backwards. The HUD has no tick source
    /// of its own; converting these to wall-clock time would mean borrowing one, and that is a separate
    /// decision from porting the machine.</para>
    ///
    /// <para><b>Not modelled: the dialogue gate.</b> The oracle only runs this timer while
    /// <c>!this.dialScript.running</c> (<c>:1947</c>), so a message list stays frozen under a dialogue
    /// instead of draining. The port's HUD has no "a dialogue is running" signal to read — <c>ScriptDialogueView</c>
    /// keeps its own running state private — so the drain runs unconditionally. The visible difference is
    /// that a line can expire during a cutscene instead of after it.</para>
    /// </summary>
    public sealed class InfoLogRules
    {
        /// <summary><c>kolStr &gt; 6</c> (<c>GUI.as:1571</c>) — the seventh line evicts the first.</summary>
        public const int MaxLines = 6;

        /// <summary><c>t_info = 150</c> on every accepted add (<c>:1570</c>).</summary>
        public const int HoldTicks = 150;

        /// <summary><c>t_info == 100</c> forgets the previous line (<c>:1957-1959</c>) — 50 ticks after the add.</summary>
        public const int RepeatWindowTicks = 100;

        /// <summary><c>t_info = 60</c> when the timer expires with lines still showing (<c>:1963</c>).</summary>
        public const int DrainTicks = 60;

        private readonly List<string> _lines = new List<string>(MaxLines);

        /// <summary>The visible lines, oldest first. Never null; at most <see cref="MaxLines"/>.</summary>
        public IReadOnlyList<string> Lines => _lines;

        /// <summary>AS3 <c>prevInfoText</c> — the last line accepted, for the de-dupe.</summary>
        public string PreviousLine { get; private set; } = string.Empty;

        /// <summary>AS3 <c>t_info</c>.</summary>
        public int Timer { get; private set; }

        /// <summary>AS3 <c>kolStr</c> — how many lines are showing.</summary>
        public int LineCount => _lines.Count;

        /// <summary>True while there is anything to draw.</summary>
        public bool HasContent => _lines.Count > 0;

        /// <summary>
        /// One <c>infoText</c> call. Returns whether the line was accepted — false when it is a repeat
        /// of the previous one, which is the oracle's <c>_loc5_ != this.prevInfoText</c> guard.
        ///
        /// <para>The empty-string case is refused too: the oracle would append an empty <c>&lt;br&gt;</c>
        /// and count it, but a missing localisation key produces exactly that, and a blank line that
        /// consumes one of the six slots is worse than a miss the caller can log.</para>
        /// </summary>
        public bool Add(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (string.Equals(text, PreviousLine, StringComparison.Ordinal)) return false;

            PreviousLine = text;
            _lines.Add(text);
            Timer = HoldTicks;

            if (_lines.Count > MaxLines) RemoveOldest();
            return true;
        }

        /// <summary>
        /// One HUD frame. Returns whether the visible text changed, so the view can skip a rebuild on
        /// the ~149 frames out of 150 where nothing happens.
        /// </summary>
        public bool Step()
        {
            if (_lines.Count == 0) return false;

            bool changed = false;
            Timer--;

            if (Timer == 0)
            {
                RemoveOldest();
                changed = true;
            }

            if (Timer == RepeatWindowTicks) PreviousLine = string.Empty;

            if (Timer <= 0 && _lines.Count > 0) Timer = DrainTicks;

            return changed;
        }

        /// <summary>AS3 <c>remStr</c> (<c>:1640-1650</c>): drop the oldest line, or everything if it is the last.</summary>
        public void RemoveOldest()
        {
            if (_lines.Count <= 1)
            {
                _lines.Clear();
                return;
            }

            _lines.RemoveAt(0);
        }

        /// <summary>Drops everything, including the de-dupe memory — the oracle's <c>remStr</c> at 1 line.</summary>
        public void Clear()
        {
            _lines.Clear();
            PreviousLine = string.Empty;
            Timer = 0;
        }
    }

    /// <summary>
    /// Which arm of <c>Invent.take</c>'s <c>if / else if</c> chain an item lands in
    /// (<c>unit/Invent.as:1060-1268</c>).
    ///
    /// <para><b>Why this enum exists instead of a table of tips.</b> The chain is <i>ordered</i> and its
    /// arms disagree about both the message and the colour. <c>armor</c> and <c>spell</c> produce
    /// <b>no message at all</b> — only a floating label — which a flat "tip → message key" table cannot
    /// express. And the last arm is a catch-all that then re-branches on <c>tip</c> a second time for
    /// its colour, so <c>key</c> and <c>spec</c> are coloured by a different rule than <c>scheme</c>
    /// even though all three are "schematics and keys" to a reader.</para>
    /// </summary>
    public enum PickupKind
    {
        /// <summary><c>tip='weapon'</c> — <c>takeWeapon</c>, colour 5.</summary>
        Weapon,

        /// <summary><c>tip='armor'</c> — <b>no info line</b>, colour 3.</summary>
        Armour,

        /// <summary><c>tip='spell'</c> — <b>no info line</b>, colour 5.</summary>
        Spell,

        /// <summary><c>tip='scheme'</c> — <c>take</c> with the bare name, colour 7.</summary>
        Scheme,

        /// <summary><c>tip='e'</c> — <c>take</c> with the quantity suffix, colour 3.</summary>
        Explosive,

        /// <summary><c>tip='a'</c> — <c>takeAmmo</c> with the count as <c>@2</c>, colour 3.</summary>
        Ammo,

        /// <summary><c>tip='med'</c> — <c>takeMed</c> with the bare name, colour 1.</summary>
        Medicine,

        /// <summary><c>tip='book'</c> — <c>takeBook</c> with the bare name, colour 4.</summary>
        Book,

        /// <summary><c>tip</c> in <c>instr</c>/<c>art</c>/<c>impl</c>, or the row carries <c>&lt;sk&gt;</c> children — colour 6.</summary>
        Skill,

        /// <summary>Everything else — colour from the second, nested <c>tip</c> table.</summary>
        Other,
    }

    /// <summary>
    /// The decision half of <c>Invent.take</c>'s feedback (<c>unit/Invent.as:1049-1315</c>) — which
    /// message an item produces, what its floating label says, and what colour both are drawn in.
    ///
    /// <para><b>Every number here was read off the oracle, and the first draft of this class got four of
    /// the ten arms wrong</b> — it assumed a single <c>tip</c>-keyed colour table, which is what the
    /// catch-all arm looks like if you read only that arm. The real chain is
    /// <c>weapon / armor / spell / scheme / e / a / med / book / (instr|art|impl|sk) / else</c>, each arm
    /// setting its own colour constant, and only the <i>last</i> arm consulting <c>tip</c> again. The
    /// branch order is load-bearing: <c>scheme</c> is tested before the <c>sk</c> catch, so a scheme row
    /// with skill children is still colour 7.</para>
    ///
    /// <para><b>Why the rules are separated from the views.</b> Every one of them is a string or a
    /// colour that can be asserted offline, and the oracle's own localisation table is the
    /// specification: <c>Resources/Localisation/text_en.xml</c> section <c>info</c> holds
    /// <c>&lt;info id="take"&gt;&lt;n&gt;Found: @1&lt;/n&gt;</c> and the five sibling templates. A view
    /// that hard-coded those strings would be a second copy of the table.</para>
    /// </summary>
    public static class PickupFeedbackRules
    {
        /// <summary>
        /// The AS3 arm for an item row. <paramref name="hasSkillRows"/> is
        /// <c>Boolean(l.xml.sk.length())</c> — the <b>presence of <c>&lt;sk&gt;</c> children on the item
        /// row</b>, not a <c>tip</c> value, is the last term of the chain before the catch-all. That is
        /// why a plain <c>stuff</c> item that happens to grant a skill is coloured like an implant.
        /// </summary>
        public static PickupKind Classify(string tip, bool hasSkillRows)
        {
            // Item.as:9-57 holds the constants; the literals are reproduced here because the port has
            // no Item class and inventing one to hold ten strings would be worse than the strings.
            switch (tip)
            {
                case "weapon": return PickupKind.Weapon;
                case "armor": return PickupKind.Armour;
                case "spell": return PickupKind.Spell;
                case "scheme": return PickupKind.Scheme;
                case "e": return PickupKind.Explosive;
                case "a": return PickupKind.Ammo;
                case "med": return PickupKind.Medicine;
                case "book": return PickupKind.Book;
                case "instr":
                case "art":
                case "impl":
                    return PickupKind.Skill;
            }

            return hasSkillRows ? PickupKind.Skill : PickupKind.Other;
        }

        /// <summary>
        /// The <c>info</c> table key for an arm, or <c>null</c> when the arm shows <b>no message at
        /// all</b> — which is the case for armour and spells (<c>Invent.as:1119-1131</c>): those two arms
        /// set a colour and return, so picking up armour produces a rising label and nothing in the list.
        /// </summary>
        public static string InfoKey(PickupKind kind, bool isMoney)
        {
            switch (kind)
            {
                case PickupKind.Weapon: return "takeWeapon";
                case PickupKind.Ammo: return "takeAmmo";
                case PickupKind.Medicine: return "takeMed";
                case PickupKind.Book: return "takeBook";
                case PickupKind.Armour:
                case PickupKind.Spell: return null;
                case PickupKind.Other: return isMoney ? "takeMoney" : "take";
                default: return "take";
            }
        }

        /// <summary>
        /// The <c>@1</c> substitution. The arms disagree, and the disagreement is not cosmetic:
        /// <c>takeMoney</c> takes the <b>count</b> (<c>infoText("takeMoney", l.kol)</c>,
        /// <c>:1234</c>), every other arm takes the name, and the <c>sk</c> arm passes the bare name
        /// with <b>no quantity suffix</b> while the catch-all appends one (<c>:1218</c> against
        /// <c>:1238</c>).
        /// </summary>
        public static string InfoFirst(PickupKind kind, bool isMoney, string displayName, int quantity)
        {
            if (kind == PickupKind.Other && isMoney)
            {
                return quantity.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            // Explosive and the catch-all append the count; scheme / skill / med / book / weapon / ammo
            // pass the bare name. Same expression as FloatText, deliberately: the oracle repeats it.
            return kind == PickupKind.Explosive || kind == PickupKind.Other
                ? FloatText(displayName, quantity)
                : (displayName ?? string.Empty);
        }

        /// <summary>
        /// The <c>@2</c> substitution, or <c>null</c> when the template has no second slot. Only two arms
        /// supply one: <c>takeWeapon</c> (condition percent, <c>:1099</c>) and <c>takeAmmo</c> (the
        /// count, <c>:1167</c>).
        /// </summary>
        public static string InfoSecond(PickupKind kind, int quantity, int conditionPercent)
        {
            switch (kind)
            {
                case PickupKind.Weapon:
                    return conditionPercent.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case PickupKind.Ammo:
                    return quantity.ToString(System.Globalization.CultureInfo.InvariantCulture);
                default:
                    return null;
            }
        }

        /// <summary>
        /// AS3's <c>@1</c>/<c>@2</c> substitution (<c>GUI.as:1555-1560</c>). <c>@1</c> is wrapped in
        /// <b>&lt;b&gt;</b>; <c>@2</c> is not. Both are single <c>replace</c> calls, so every occurrence
        /// is replaced and a name that itself contains "@1" cannot recurse.
        /// </summary>
        public static string Substitute(string template, string first, string second)
        {
            if (string.IsNullOrEmpty(template)) return string.Empty;

            string result = template;
            if (first != null) result = result.Replace("@1", "<b>" + first + "</b>");
            if (second != null) result = result.Replace("@2", second);
            return result;
        }

        /// <summary>
        /// The floating label — <c>gui.floatText(l.nazv + (l.kol &gt; 1 ? " (" + l.kol + ")" : ""), …)</c>
        /// (<c>Invent.as:1291</c>). The name is <b>not</b> wrapped in anything; the brackets are the
        /// oracle's own ASCII ones. Unlike the message, this expression is the same for every arm.
        /// </summary>
        public static string FloatText(string displayName, int quantity)
        {
            string name = displayName ?? string.Empty;
            return quantity > 1
                ? name + " (" + quantity.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")"
                : name;
        }

        /// <summary>
        /// The floating label's colour index — <c>l.fc</c> when the row carries one, else the arm's
        /// constant, else (for the catch-all only) a second <c>tip</c> table.
        ///
        /// <para><b>The per-arm constants are the part a reader gets wrong.</b> They are set inline in
        /// each branch and are <i>not</i> the values the catch-all's table would give: a weapon is 5, not
        /// 1; a schematic is 7, not 6; ammunition and explosives are 3; medicine is 1; a book is 4. Only
        /// <c>key</c>/<c>spec</c> — which reach the catch-all, because no arm tests for them — get 6, and
        /// <c>scheme</c> is tested <i>before</i> the <c>sk</c> catch so it keeps 7.</para>
        ///
        /// <para><paramref name="forcedColour"/> is <c>l.fc</c>, applied to every arm
        /// (<c>Invent.as:1289-1291</c>); <c>-1</c> means the row has none.</para>
        /// </summary>
        public static int FloatColour(PickupKind kind, string tip, int forcedColour)
        {
            if (forcedColour >= 0) return forcedColour;

            switch (kind)
            {
                case PickupKind.Weapon: return 5;
                case PickupKind.Armour: return 3;
                case PickupKind.Spell: return 5;
                case PickupKind.Scheme: return 7;
                case PickupKind.Explosive: return 3;
                case PickupKind.Ammo: return 3;
                case PickupKind.Medicine: return 1;
                case PickupKind.Book: return 4;
                case PickupKind.Skill: return 6;
            }

            // The catch-all's nested table (Invent.as:1240-1268). "equip" is a bare literal in the
            // oracle — there is no L_EQUIP constant — and "valuables" likewise.
            switch (tip)
            {
                case "valuables": return 2;
                case "him":
                case "pot": return 1;
                case "key":
                case "spec": return 6;
                case "equip": return 8;
                default: return 0;
            }
        }

        /// <summary>
        /// The Flash text-format colour for an index — <c>GUI.as:192-208</c>, styles <c>.r0</c> through
        /// <c>.r9</c>. The oracle applies these as a <b>style sheet on rich text</b>
        /// (<c>&lt;span class='r6'&gt;…&lt;/span&gt;</c>, <c>GUI.as:1610-1613</c>); the port has one
        /// label per colour so it needs the resolved colour instead.
        ///
        /// <para><c>.r</c> itself (no digit) is <c>#00FF99</c> and is the default body colour, not an
        /// index — an out-of-range index therefore falls back to <c>r0</c>'s grey rather than to it.</para>
        ///
        /// <para><b>The hex values are not repeated here.</b> <c>PfeUiColors</c> already exposes every
        /// one of them, resolved from <c>FlashTextStyle</c>, which is the single owner of the sheet
        /// (<c>GUI.as:192-208</c>). A second copy of ten hex literals is how the two would drift.</para>
        /// </summary>
        public static Color FlashColour(int index)
        {
            switch (index)
            {
                case 1: return PFE.UI.Common.PfeUiColors.Cyan;
                case 2: return PFE.UI.Common.PfeUiColors.Yellow;
                case 3: return PFE.UI.Common.PfeUiColors.Orange;
                case 4: return PFE.UI.Common.PfeUiColors.Magenta;
                case 5: return PFE.UI.Common.PfeUiColors.Red;
                case 6: return PFE.UI.Common.PfeUiColors.Violet;
                case 7: return PFE.UI.Common.PfeUiColors.Tan;
                case 8: return PFE.UI.Common.PfeUiColors.Olive;
                case 9: return PFE.UI.Common.PfeUiColors.Blue;
                default: return PFE.UI.Common.PfeUiColors.Grey;
            }
        }

        /// <summary>
        /// AS3's gate on the modal (<c>Invent.as:1293-1300</c>):
        /// <c>if(World.w.helpMess || l.tip == "art") if(l.mess != null &amp;&amp; triggers["mess_"+l.mess] &lt;= 0)</c>.
        ///
        /// <para><b>Two independent conditions, and both are needed.</b> <c>helpMess</c> is the
        /// tutorial-messages option; <c>tip == "art"</c> bypasses it, because a stat-raising artefact's
        /// description is not a tutorial. The latch is per <c>mess</c> key, not per item id — two items
        /// sharing a mess text show it once between them.</para>
        /// </summary>
        public static bool ShowsImportantMessage(bool helpMess, string tip, string messKey, bool alreadyShown)
        {
            if (string.IsNullOrEmpty(messKey)) return false;
            if (alreadyShown) return false;
            return helpMess || string.Equals(tip, "art", StringComparison.Ordinal);
        }

        /// <summary>The oracle's trigger key for the once-per-save latch — <c>"mess_" + l.mess</c>.</summary>
        public static string MessTriggerKey(string messKey) => "mess_" + messKey;

        /// <summary>
        /// AS3 <c>Math.round</c>, which is <c>floor(x + 0.5)</c> — <b>not</b> banker's rounding. The
        /// condition percent in <c>takeWeapon</c> is built with it (<c>Invent.as:1099</c>), so a 0.5 %
        /// difference between the two conventions is visible in the message.
        /// </summary>
        public static int RoundToPercent(float fraction) => (int)Math.Floor(fraction * 100f + 0.5f);
    }
}
