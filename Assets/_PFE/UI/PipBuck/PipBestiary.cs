using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;

namespace PFE.UI.PipBuck
{
    /// <summary>One row of the bestiary — the port of <c>setStatItem</c>'s <c>page2 == 5</c> arm
    /// (<c>PipPageInfo.as:336-356</c>).</summary>
    public sealed class PipBestiaryRow
    {
        /// <summary>The unit id — the row's hidden <c>id</c> field and the detail pane's key.</summary>
        public string UnitId = "";

        /// <summary>AS3 <c>Res.txt("u", id)</c>.</summary>
        public string Name = "";

        /// <summary>AS3 <c>@cat</c>, kept as the <b>string</b> it is: every branch compares it to
        /// <c>"1"</c>, <c>"2"</c> or <c>"3"</c>, and <c>cat='1'</c> is not <c>cat='01'</c>.</summary>
        public string Category = "";

        /// <summary>
        /// AS3 <c>kol</c> — how many of this creature the player has killed, or <c>-1</c> for
        /// "not counted".
        ///
        /// <para><b>Two different states share <c>-1</c> and one of them is filtered out.</b> Every row
        /// starts at <c>-1</c>; a <c>cat="3"</c> row is raised to its <c>frag_&lt;id&gt;</c> count when
        /// that trigger exists and is non-negative; and a <c>cat="2"</c> row is raised only when a
        /// following <c>cat="3"</c> child contributed a count. <c>isKol</c> then keeps a row when
        /// <c>kol &gt;= 0</c> <b>or</b> <c>cat == "1"</c> — so an <b>uncounted faction is dropped</b>
        /// and only the templates (cat 1) survive with <c>-1</c>. That is worth stating because it reads
        /// like the opposite: a faction with no counted members does <i>not</i> list.</para>
        /// </summary>
        public int Kills = -1;

        /// <summary>
        /// AS3 <c>n.prev</c> — the id of the faction this row was rolled up under.
        ///
        /// <para><b>Written by the build and read by nothing.</b> <c>PipPageInfo.as:285</c> sets it and
        /// no other line in either page class reads it — the indent comes from <c>@cat</c>, not from
        /// this. Carried here so the build is a faithful port and so the dead field is <i>visible</i>
        /// rather than quietly dropped; a fixture pins that it is populated.</para>
        /// </summary>
        public string PreviousId = "";

        /// <summary>The caption indent — <c>"      "</c> for <c>cat="2"</c>, <c>"            "</c> for
        /// <c>cat="3"</c>, none for <c>cat="1"</c> (<c>PipPageInfo.as:339-350</c>).</summary>
        public int Indent;

        /// <summary>Whether the caption is bold — <c>&lt;b&gt;</c> for <c>cat</c> 1 and 2, not for 3.</summary>
        public bool Bold;

        /// <summary>The caption column's x — <c>param1.nazv.x = 5</c> (<c>PipPageInfo.as:338</c>).</summary>
        public float NameX = PipInfoFrame.EnemyNameX;

        /// <summary>Whether the row shows a count at all — <c>if(param2.kol &gt; 0)</c>
        /// (<c>PipPageInfo.as:351-354</c>). Note the count field is made <i>visible</i> regardless; only
        /// its text is conditional.</summary>
        public bool ShowCount => Kills > 0;
    }

    /// <summary>
    /// Sub-tab 5 — the bestiary. The port of <c>PipPageInfo</c>'s <c>page2 == 5</c> build
    /// (<c>:243-292</c>), of <c>setStatItem</c>'s enemy arm (<c>:336-356</c>) and of
    /// <c>infoUnit</c>/<c>vulner</c>/<c>getParam</c> (<c>:461-714</c>).
    ///
    /// <para><b>This reads the raw <c>AllData</c> document, not the imported unit assets — and that is
    /// the faithful choice, not a shortcut.</b> <c>infoUnit</c> is not a table read: it asks
    /// <c>getParam</c> whether an attribute <i>exists</i> on the unit's <c>&lt;comb&gt;</c>,
    /// <c>&lt;vis&gt;</c> or <c>&lt;vulner&gt;</c> node, and prints the line only when it does
    /// (<c>if(v_skin)</c>, <c>if(v_dexter != null)</c>). A port over <c>UnitDefinition</c> cannot answer
    /// that question: its fields are typed values with defaults, so <c>armor='0'</c> and an absent
    /// <c>armor</c> are the same <c>0</c> — and the difference is a whole line on screen. The raw
    /// document is already available at runtime
    /// (<c>Resources.Load&lt;TextAsset&gt;("Loot/AllData")</c>, <c>LootRuntime.cs:159</c>), and
    /// <c>AllDataXml</c> already extracts and parses it, so the port can ask the same question the
    /// Oracle asks of the same bytes.</para>
    ///
    /// <para><b>The three kill gates are the whole feature.</b> A creature's stat block is revealed in
    /// proportion to how many the player has killed: <c>n = floor(kills / delta)</c> where
    /// <c>delta</c> is the unit's <c>&lt;vis dkill&gt;</c> (absent means <b>5</b>, and a value
    /// <c>&lt;= 0</c> means "already fully known", giving <c>n = 10</c>). <c>n &gt;= 1</c> reveals
    /// health, skin and armour; <c>n &gt;= 2</c> reveals melee and ranged damage and the weapon list;
    /// <c>n &gt;= 3</c> reveals dexterity, observation, weapon skill and the twelve-type resist line.
    /// Ten of the 148 shipped units author <c>dkill='0'</c>, so their full block is visible from the
    /// first encounter.</para>
    /// </summary>
    public static class PipBestiary
    {
        /// <summary>AS3's default when a unit authors no <c>&lt;vis dkill&gt;</c>
        /// (<c>PipPageInfo.as:519-523</c>).</summary>
        public const int DefaultKillDelta = 5;

        /// <summary>The <c>n</c> a unit with <c>dkill &lt;= 0</c> is treated as having — "fully known"
        /// (<c>PipPageInfo.as:524-527</c>).</summary>
        public const int FullyKnownKills = 10;

        /// <summary>
        /// The twelve damage types the resist line prints, <b>in the Oracle's display order</b>, paired
        /// with the <c>&lt;vulner&gt;</c> attribute that feeds each.
        ///
        /// <para><b>Not numeric order, and that is the point.</b> <c>PipPageInfo.as:659-706</c> tests
        /// <c>emp</c> first and <c>acid</c> last: emp, bullet, blade, blunt, explosion, laser, plasma,
        /// fire, freezing, lightning, poison, acid — which as AS3's constants is
        /// 8, 0, 1, 2, 4, 5, 6, 3, 11, 9, 7, 10. Sorting by constant would reorder the line on screen.
        /// The label for each is <c>Res.pipText("tipdam" + type)</c>, and all twelve keys exist
        /// (<c>tipdam0</c>…<c>tipdam11</c>).</para>
        /// </summary>
        public static readonly (int Type, string Attribute)[] ResistOrder =
        {
            (8,  "emp"),
            (0,  "bul"),
            (1,  "blade"),
            (2,  "phis"),
            (4,  "expl"),
            (5,  "laser"),
            (6,  "plasma"),
            (3,  "fire"),
            (11, "cryo"),
            (9,  "spark"),
            (7,  "venom"),
            (10, "acid"),
        };

        /// <summary>
        /// The bestiary list — the port of <c>PipPageInfo.as:243-292</c>.
        ///
        /// <para><b>The roll-up is a single forward pass with one mutable cursor.</b> <c>prevObj</c>
        /// holds the most recent <c>cat="2"</c> row; every following <c>cat="3"</c> row that has a count
        /// adds it into that faction's total and records the faction as its <c>prev</c>. So a faction's
        /// displayed number is the sum of its counted members <b>and nothing else</b> — a member with no
        /// <c>frag_</c> trigger contributes nothing and does not even mark the faction as counted.</para>
        ///
        /// <para>Only rows with a non-empty <c>@cat</c> are considered, so the ~90 units that are pure
        /// variants without a category never appear.</para>
        /// </summary>
        /// <param name="allDataRoot">The parsed <c>&lt;all&gt;</c> root of <c>AllData.txt</c>.</param>
        /// <param name="text">The text table, for each unit's name.</param>
        /// <param name="fragCount">Reads <c>triggers["frag_" + id]</c>. Return a negative number for
        /// "the trigger is absent" — AS3's <c>undefined &gt;= 0</c> is false, and a stored <c>0</c> is
        /// <i>not</i> the same thing.</param>
        public static List<PipBestiaryRow> BuildList(
            XElement allDataRoot,
            PipTextTable text,
            Func<string, int> fragCount)
        {
            var rows = new List<PipBestiaryRow>();
            if (allDataRoot == null) return rows;

            PipBestiaryRow previous = null;

            foreach (XElement unit in allDataRoot.Elements("unit"))
            {
                string id = (string)unit.Attribute("id");
                string category = (string)unit.Attribute("cat");

                // `if(xml && Boolean(xml.@cat.length()))`
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(category)) continue;

                var row = new PipBestiaryRow
                {
                    UnitId = id,
                    Name = text?.Unit(id) ?? id,
                    Category = category,
                    Kills = -1,
                };

                // `if(xml.@cat == "3" && triggers["frag_" + id] >= 0) n.kol = int(…);`
                if (category == "3")
                {
                    int frag = fragCount?.Invoke("frag_" + id) ?? -1;
                    if (frag >= 0) row.Kills = frag;
                }

                if (category == "2")
                {
                    previous = row;
                }
                else if (category == "3")
                {
                    if (previous != null && row.Kills >= 0)
                    {
                        // `if(prevObj.kol < 0) prevObj.kol = 0; prevObj.kol += n.kol;`
                        if (previous.Kills < 0) previous.Kills = 0;
                        previous.Kills += row.Kills;
                    }

                    if (previous != null) row.PreviousId = previous.UnitId;
                }

                rows.Add(row);
                ApplyCaptionStyle(row);
            }

            // `arr.filter(this.isKol)` — `kol >= 0 || cat == "1"`.
            var kept = new List<PipBestiaryRow>(rows.Count);
            foreach (PipBestiaryRow row in rows)
            {
                if (row.Kills >= 0 || row.Category == "1") kept.Add(row);
            }

            return kept;
        }

        /// <summary>
        /// The indent and boldness a row's caption carries — <c>PipPageInfo.as:339-350</c>. Kept as its
        /// own function because the indents are literal space runs in the Oracle (six and twelve) and a
        /// fixture can pin them without a font.
        /// </summary>
        public static void ApplyCaptionStyle(PipBestiaryRow row)
        {
            if (row == null) return;

            switch (row.Category)
            {
                case "1":
                    row.Indent = 0;
                    row.Bold = true;
                    break;
                case "2":
                    row.Indent = 6;
                    row.Bold = true;
                    break;
                case "3":
                    row.Indent = 12;
                    row.Bold = false;
                    break;
                default:
                    row.Indent = 0;
                    row.Bold = false;
                    break;
            }
        }

        /// <summary>
        /// The caption a row draws — the Oracle's <c>"&lt;b&gt;" + text + "&lt;/b&gt;"</c> /
        /// <c>"      &lt;b&gt;" + text + "&lt;/b&gt;"</c> / <c>"            " + text</c>
        /// (<c>PipPageInfo.as:339-350</c>), which is what <c>htmlText</c> receives.
        /// </summary>
        public static string Caption(PipBestiaryRow row)
        {
            if (row == null) return "";

            ApplyCaptionStyle(row);
            string indent = new string(' ', row.Indent);
            return row.Bold ? indent + "<b>" + row.Name + "</b>" : indent + row.Name;
        }

        /// <summary>
        /// The detail pane's body — <c>Res.txt("u", id, 1)</c> followed by
        /// <see cref="InfoUnit"/> (<c>PipPageInfo.as:445</c>).
        /// </summary>
        /// <param name="kills">The row's <c>kol</c>, i.e. what the count column holds. Pass the row's
        /// <see cref="PipBestiaryRow.Kills"/>; a non-positive value is treated as "no count", which is
        /// what the Oracle does by passing the count field's <i>text</i> — the field is left empty when
        /// the count is not positive, and <c>int("")</c> is <c>0</c>.</param>
        public static string BuildDetail(
            XElement allDataRoot,
            PipTextTable text,
            string unitId,
            int kills)
        {
            if (allDataRoot == null || string.IsNullOrEmpty(unitId)) return "";

            XElement unit = FindUnit(allDataRoot, unitId);
            if (unit == null) return "";

            string info = text?.TextInTable("unit", unitId, 1) ?? "";
            return info + "\n" + InfoUnit(allDataRoot, text, unit, kills);
        }

        /// <summary>
        /// AS3 <c>infoUnit(id, kol)</c> (<c>PipPageInfo.as:482-709</c>).
        ///
        /// <para><b>Returns the empty string for anything that is not a <c>cat="3"</c> unit</b>
        /// (<c>:511-514</c>) — so a template or a faction shows its name and its <c>&lt;info&gt;</c>
        /// text and no stat block, even though the list contains it. That is the Oracle's behaviour and
        /// it is why the templates are useful as section headers rather than as entries.</para>
        ///
        /// <para><b>Everything printed is gated twice.</b> Once on the kill count <c>n</c> and once on
        /// whether the attribute exists at all, and the two are independent — a unit with
        /// <c>dkill='0'</c> and no <c>armor</c> attribute prints health and nothing else, because
        /// <c>if(v_armor)</c> is a <i>presence</i> test on the attribute, not a truthiness test on its
        /// value.</para>
        /// </summary>
        /// <param name="allDataRoot">The parsed root, so the parent unit and the weapon table can be
        /// resolved — AS3's <c>AllData.d.unit.(@id == un.@parent)</c> and
        /// <c>AllData.d.weapon.(@id == weap.@id)</c>.</param>
        public static string InfoUnit(XElement allDataRoot, PipTextTable text, XElement unit, int kills)
        {
            if (unit == null) return "";
            if ((string)unit.Attribute("cat") != "3") return "";

            string parentId = (string)unit.Attribute("parent");
            XElement parent = string.IsNullOrEmpty(parentId) ? null : FindUnit(allDataRoot, parentId);

            // `delta = getParam(un, pun, "vis", "dkill"); if(delta == null) delta = 5;`
            string deltaRaw = GetParam(unit, parent, "vis", "dkill");
            int delta = deltaRaw == null ? DefaultKillDelta : ParseInt(deltaRaw);

            // `if(delta <= 0) n = 10; else n = Math.floor(int(kol) / delta);`
            // `kol` arrives as the count field's *text*, which the row leaves empty unless the count is
            // positive — and `int("")` is 0.
            int killCount = kills > 0 ? kills : 0;
            int n = delta <= 0 ? FullyKnownKills : (int)Math.Floor(killCount / (double)delta);

            string hp = GetParam(unit, parent, "comb", "hp");
            string skin = GetParam(unit, parent, "comb", "skin");
            string aqual = GetParam(unit, parent, "comb", "aqual");
            string armor = GetParam(unit, parent, "comb", "armor");
            string marmor = GetParam(unit, parent, "comb", "marmor");
            string dexter = GetParam(unit, parent, "comb", "dexter");
            string skill = GetParam(unit, parent, "comb", "skill");
            string observ = GetParam(unit, parent, "comb", "observ");
            string visdam = GetParam(unit, parent, "vis", "visdam");
            string damage = GetParam(unit, parent, "comb", "damage");
            string tipdam = GetParam(unit, parent, "comb", "tipdam");
            string sdamage = GetParam(unit, parent, "vis", "sdamage");
            string stipdam = GetParam(unit, parent, "vis", "stipdam");

            var builder = new System.Text.StringBuilder();
            builder.Append('\n');

            // `if(un.comb.length())` — note the Oracle assigns `node = un.comb[0]` here and never reads
            // it; the local is dead in this branch.
            if (unit.Element("comb") != null)
            {
                if (n >= 1)
                {
                    // `yel(v_hp)` with no presence test, because the data guarantees one: all 104
                    // `cat="3"` units carry a `<comb>` and every one of those authors `hp`. (AS3 would
                    // print the literal word "null" for an absent one — string concatenation of
                    // `null` — so if a future data edit removes an `hp`, this line is where to look.)
                    builder.Append(Pip(text, "hp")).Append(": ").Append(PipPageMarkup.Yellow(hp)).Append('\n');

                    if (skin != null)
                    {
                        builder.Append(Pip(text, "skin")).Append(": ").Append(PipPageMarkup.Yellow(skin)).Append('\n');
                    }

                    // `if(v_aqual)` — presence, so `aqual='0'` still enters and still prints "(0%)".
                    if (aqual != null)
                    {
                        // **Double, not float, and the Oracle's own lack of rounding.** `v_aqual * 100`
                        // is printed raw (PipPageInfo.as:560), so the digits are whatever AS3's
                        // double arithmetic produces. Parsing to `float` first would give 0.65f =
                        // 0.64999997… and print "64.9999976158142%" where the Oracle prints "65%".
                        // Every authored value in AllData.as — 1, 0.9, 0.85, 0.8, 0.75, 0.7, 0.65, 0.5,
                        // 0.4, 0.25 — is exact under double multiplication, so the raw print is clean.
                        string percent = " (" + (ParseDouble(aqual) * 100.0).ToString(CultureInfo.InvariantCulture) + "%)";

                        if (armor != null)
                        {
                            builder.Append(Pip(text, "armor")).Append(": ").Append(PipPageMarkup.Yellow(armor))
                                   .Append(percent).Append("  ");
                        }

                        if (marmor != null)
                        {
                            builder.Append(Pip(text, "marmor")).Append(": ").Append(PipPageMarkup.Yellow(marmor))
                                   .Append(percent);
                        }

                        if (armor != null || marmor != null) builder.Append('\n');
                    }
                }

                if (n >= 2)
                {
                    int visdamValue = visdam == null ? int.MinValue : ParseInt(visdam);

                    if ((visdamValue == 1 || visdamValue == 3) && damage != null)
                    {
                        builder.Append(Pip(text, "dam_melee")).Append(": ");
                        builder.Append(PipPageMarkup.Blue(Pip(text, "tipdam" + (tipdam ?? "2"))));
                        builder.Append(" (").Append(PipPageMarkup.Yellow(damage)).Append(")\n");
                    }

                    if ((visdamValue == 2 || visdamValue == 3) && sdamage != null)
                    {
                        builder.Append(Pip(text, "dam_shoot")).Append(": ");
                        builder.Append(PipPageMarkup.Blue(Pip(text, "tipdam" + (stipdam ?? "0"))));
                        builder.Append(" (").Append(PipPageMarkup.Yellow(sdamage)).Append(")\n");
                    }

                    AppendWeapons(builder, allDataRoot, text, unit);
                }

                if (n >= 3)
                {
                    // `if(v_dexter != null)` — the only two parameters the Oracle tests against null
                    // rather than truthiness, because 0 is a meaningful value for both.
                    if (dexter != null)
                    {
                        double d = ParseDouble(dexter);
                        builder.Append(Pip(text, "dexter")).Append(": ")
                               .Append(PipPageMarkup.Yellow((d > 1.0 ? "+" : "") + As3Round((d - 1.0) * 100.0) + "%"))
                               .Append('\n');
                    }

                    // `if(v_observ)` — a **presence** test on the attribute string, not a value test.
                    // AS3's `if("0")` is true, so a unit that authors `observ='0'` really does print
                    // `observation: 0`, and a port that tested the parsed number would drop the line.
                    // The value printed is the raw string, so `observ='5.0'` stays "5.0" — which is
                    // also why the sign test parses but the concatenation does not.
                    if (observ != null)
                    {
                        int o = ParseInt(observ);
                        builder.Append(Pip(text, "observ")).Append(": ")
                               .Append(PipPageMarkup.Yellow((o > 0 ? "+" : "") + observ))
                               .Append('\n');
                    }

                    if (skill != null)
                    {
                        builder.Append(Pip(text, "weapskill")).Append(": ")
                               .Append(PipPageMarkup.Yellow(As3Round(ParseDouble(skill) * 100.0) + "%"))
                               .Append('\n');
                    }
                }
            }

            // `if(n >= 3 && Boolean(un.vulner.length()))`
            if (n >= 3)
            {
                XElement vulner = unit.Element("vulner");
                if (vulner != null)
                {
                    builder.Append(Pip(text, "resists")).Append(": ");

                    foreach ((int type, string attribute) in ResistOrder)
                    {
                        XAttribute value = vulner.Attribute(attribute);
                        if (value == null) continue;

                        double factor = ParseDouble(value.Value);
                        builder.Append(PipPageMarkup.Blue(Pip(text, "tipdam" + type)))
                               .Append(": ")
                               .Append(PipPageMarkup.Yellow(As3Round((1.0 - factor) * 100.0) + "%   "));
                    }
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// The weapon list inside the <c>n &gt;= 2</c> block (<c>PipPageInfo.as:600-637</c>).
        ///
        /// <para><b>Two filters and one computation.</b> A row is skipped when <c>@no &gt; 0</c> — the
        /// "do not list this one" flag — and the damage in brackets is the weapon's <c>&lt;char&gt;</c>
        /// row's <c>damage</c> plus <c>damexpl</c>, printed through <c>Res.numb</c>. The lookup is
        /// wrapped in <c>try/catch</c> in the Oracle, so a weapon id with no <c>&lt;weapon&gt;</c> row
        /// prints its name with no number rather than failing; that is reproduced by simply skipping the
        /// bracket when the row is absent.</para>
        /// </summary>
        private static void AppendWeapons(
            System.Text.StringBuilder builder,
            XElement allDataRoot,
            PipTextTable text,
            XElement unit)
        {
            bool wroteHeader = false;
            int weaponRows = 0;

            foreach (XElement weapon in unit.Elements("w"))
            {
                weaponRows++;

                // `if(weap.@no <= 0)` — an absent @no reads as 0 through AS3's coercion, so only a
                // positive value skips the row.
                string no = (string)weapon.Attribute("no");
                if (no != null && ParseInt(no) > 0) continue;

                string weaponId = (string)weapon.Attribute("id");
                if (string.IsNullOrEmpty(weaponId)) continue;

                builder.Append(wroteHeader ? ", " : Pip(text, "enemy_weap") + ": ");
                builder.Append(PipPageMarkup.Blue(text?.Weapon(weaponId) ?? weaponId));

                XElement weaponRow = FindWeapon(allDataRoot, weaponId);
                XElement character = weaponRow?.Element("char");
                if (character != null)
                {
                    double damage = 0.0;
                    string rawDamage = (string)character.Attribute("damage");
                    string rawExpl = (string)character.Attribute("damexpl");

                    // `if(w.char[0].@damage > 0) dam += Number(…);` — a *value* test here, unlike the
                    // presence tests above: a negative or zero damage contributes nothing.
                    if (rawDamage != null && ParseDouble(rawDamage) > 0.0) damage += ParseDouble(rawDamage);
                    if (rawExpl != null && ParseDouble(rawExpl) > 0.0) damage += ParseDouble(rawExpl);

                    builder.Append(" (").Append(PipPageMarkup.Yellow(PipBuckReadoutFormat.Numb((float)damage))).Append(")");
                }

                wroteHeader = true;
            }

            // `s += "\n";` — emitted whenever the unit has any `<w>` child, even when every one of them
            // was skipped by @no. That leaves a bare blank line, which is the Oracle's behaviour.
            if (weaponRows > 0) builder.Append('\n');
        }

        /// <summary>
        /// AS3 <c>getParam(un, pun, node, attr)</c> (<c>PipPageInfo.as:461-480</c>): the unit's own
        /// attribute, else the parent's, else <c>null</c>.
        ///
        /// <para><b>Returns the raw attribute <i>string</i>, because the Oracle does.</b> That is not a
        /// detail to tidy away: <c>if(v_skin)</c> is a truthiness test on a String, so
        /// <c>skin='0'</c> passes and prints <c>0</c>, while an absent attribute is <c>null</c> and
        /// prints nothing. Converting to a number here would make <c>0</c> and absent indistinguishable
        /// and silently drop lines.</para>
        /// </summary>
        public static string GetParam(XElement unit, XElement parent, string node, string attribute)
        {
            string own = AttributeOf(unit, node, attribute);
            if (own != null) return own;

            return AttributeOf(parent, node, attribute);
        }

        private static string AttributeOf(XElement element, string node, string attribute)
        {
            if (element == null || string.IsNullOrEmpty(node) || string.IsNullOrEmpty(attribute)) return null;
            return (string)element.Element(node)?.Attribute(attribute);
        }

        /// <summary>AS3 <c>AllData.d.unit.(@id == id)</c>, or <c>null</c>.</summary>
        public static XElement FindUnit(XElement allDataRoot, string unitId)
        {
            if (allDataRoot == null || string.IsNullOrEmpty(unitId)) return null;

            foreach (XElement unit in allDataRoot.Elements("unit"))
            {
                if ((string)unit.Attribute("id") == unitId) return unit;
            }

            return null;
        }

        /// <summary>AS3 <c>AllData.d.weapon.(@id == id)</c>, or <c>null</c>.</summary>
        public static XElement FindWeapon(XElement allDataRoot, string weaponId)
        {
            if (allDataRoot == null || string.IsNullOrEmpty(weaponId)) return null;

            foreach (XElement weapon in allDataRoot.Elements("weapon"))
            {
                if ((string)weapon.Attribute("id") == weaponId) return weapon;
            }

            return null;
        }

        /// <summary>
        /// AS3's <c>Math.round</c> — round half toward <b>+Infinity</b>, on a <c>double</c>.
        ///
        /// <para><b>Not <c>Mathf.Round</c>, and not <c>PipBuckReadoutFormat.As3Round</c> either.</b>
        /// Unity's <c>Mathf.Round</c> is banker's rounding, so <c>Mathf.Round(0.5f) == 0</c>. The
        /// status panel's <c>As3Round</c> is correct but takes a <c>float</c>, and the values here come
        /// out of double arithmetic — <c>(dexter - 1) * 100</c> for <c>dexter='0.9'</c> is
        /// <c>-9.999999999999998</c>, which a float round-trip happens to survive but a different
        /// input might not. AS3's Numbers are doubles, so this works in doubles.</para>
        /// </summary>
        private static int As3Round(double value) => (int)Math.Floor(value + 0.5);

        private static int ParseInt(string raw) =>
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : 0;

        /// <summary>
        /// AS3's <c>Number(string)</c>. <b>Double, not float</b> — see the <c>aqual</c> percent above
        /// for the case that makes this visible.
        /// </summary>
        private static double ParseDouble(string raw) =>
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                ? value
                : 0.0;

        private static string Pip(PipTextTable text, string id) => text?.Pip(id) ?? "*pip_" + id;
    }
}
