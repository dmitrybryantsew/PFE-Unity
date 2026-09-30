using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PFE.Data.Definitions
{
    /// <summary>
    /// One <c>&lt;armor&gt;</c> element of <c>AllData.as</c>, parsed but not yet applied to an asset.
    ///
    /// <para>Exists so the parsing can be unit-tested: <c>PFE.Tests</c> references <c>PFE.Core</c> but
    /// <b>not</b> <c>PFE.Editor</c>, so an importer that both parsed and wrote assets could only be
    /// tested by running Unity. The split is the usual one — <see cref="ArmourDataParser"/> is pure,
    /// and <c>ArmourDataImporter</c> is the thin <c>#if UNITY_EDITOR</c> shell that writes the
    /// result to disk.</para>
    ///
    /// <para>Oracle: <c>Armor.as</c> — the constructor (<c>:72-193</c>) for the per-item half and
    /// <c>getXmlParam()</c> (<c>:196-280</c>) for the per-level half.</para>
    /// </summary>
    public sealed class ArmourDefinitionData
    {
        /// <summary>AS3 <c>&lt;armor id='…'&gt;</c>. Required — a block without one is a parse error.</summary>
        public string id;

        /// <summary>AS3 <c>@price</c>. Read <b>unconditionally</b> by <c>Armor.as:157</c>.</summary>
        public int price;

        /// <summary>AS3 <c>@sort</c> — inventory ordering.</summary>
        public int sortOrder;

        /// <summary>
        /// AS3 <c>@hp</c> — the item's own durability ceiling (<c>Armor.as:105-107</c>), <b>not</b> the
        /// unit pool's <c>@armorhp</c>.
        ///
        /// <para><b>0 means "absent", which is a documented convention rather than AS3's own
        /// behaviour.</b> AS3 keeps a field default of <c>100</c> and only overwrites it when the
        /// attribute is present, so <c>hp='0'</c> and "no <c>hp</c>" are distinguishable there.
        /// Here they are not, and <see cref="GameArmorInstance"/> resolves <c>0</c> back to <c>100</c>.
        /// Unreachable from the real data — no armour in <c>AllData</c> declares <c>hp='0'</c> — but it
        /// is a divergence, so it is written down.</para>
        /// </summary>
        public int armorHP;

        /// <summary>
        /// AS3 <c>@tip</c> — the equip slot. Default <c>1</c> (body armour, <c>Armor.as:16</c>);
        /// <c>3</c> is an amulet. Drives <c>UnitPlayer.changeArmor()</c>'s dispatch
        /// (<c>:3790</c> vs <c>:3824</c>) <b>and</b> the pink-resistance rule below.
        /// </summary>
        public int tip = 1;

        /// <summary>AS3 <c>@hide</c> → <c>Armor.hideMane</c> (<c>Armor.as:170-172</c>).</summary>
        public bool hideMane;

        /// <summary>AS3 <c>@und</c> → <c>Armor.und</c>, a presence flag, not a value (<c>Armor.as:113-116</c>).</summary>
        public bool indestructible;

        /// <summary>
        /// AS3 <c>@norep</c> → <c>Armor.norep</c>, a presence flag (<c>Armor.as:117-119</c>).
        ///
        /// <para>An earlier pass recorded this as dead XML data. <b>That was wrong</b>:
        /// <c>PipPageWork.as:258</c> reads it (<c>if(!a.norep &amp;&amp; !a.und &amp;&amp; a.hp &lt; a.maxhp)</c>)
        /// to decide whether the repair action is offered. It matters here because the port <i>has</i>
        /// a repair path (<see cref="GameArmorInstance.Repair"/>), so dropping it would make
        /// <c>tre</c> — the one item that is <c>norep</c> without also being <c>und</c> — repairable
        /// when the oracle says it is not.</para>
        /// </summary>
        public bool noRepair;

        /// <summary>
        /// Per-upgrade-level stats, <b>index == level</b>, from the <c>&lt;upd&gt;</c> children. Never
        /// null and never empty: an <c>&lt;armor&gt;</c> with no <c>&lt;upd&gt;</c> gets a single
        /// all-zero level plus a warning, so <c>levels[0]</c> is always safe to read.
        /// </summary>
        public EquipmentData[] levels;

        /// <summary>
        /// Everything the parse noticed and deliberately did <b>not</b> carry, plus anything it did not
        /// recognise. Each entry is prefixed with the item id.
        ///
        /// <para>This is the point of the type. AS3's armour XML carries 20 attributes on
        /// <c>&lt;armor&gt;</c> and 24 on <c>&lt;upd&gt;</c>; the port models eleven of them. A parser
        /// that silently took what it knew would make "we chose not to port the sneak channel" and
        /// "we forgot <c>radx</c>" look identical — so every drop is named, and an attribute that is in
        /// neither the handled nor the known-ignored set produces a different, louder warning.</para>
        /// </summary>
        public List<string> warnings = new List<string>();

        /// <summary>Highest index in <see cref="levels"/> — AS3 <c>Armor.maxlvl</c>, derived.</summary>
        public int MaxLevel => levels != null && levels.Length > 0 ? levels.Length - 1 : 0;

        /// <summary>The level-0 stats. Always valid.</summary>
        public EquipmentData Level0 => levels != null && levels.Length > 0 ? levels[0] : default;

        /// <summary>
        /// Write this parse into an <see cref="ItemDefinition"/>.
        ///
        /// <para><b>The one place the <c>equipment</c> / <c>armourLevels</c> invariant is
        /// established</b> — <c>equipment = levels[0]</c>, so the two cannot disagree in anything the
        /// importer produces. <c>ArmourDataParserTests</c> asserts it holds.</para>
        /// </summary>
        public void ApplyTo(ItemDefinition target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (levels == null || levels.Length == 0)
                throw new InvalidOperationException($"{id}: ApplyTo called with no levels.");

            target.itemId = id;
            target.displayName = id; // matches AmmoDataImporter's convention; localisation is a separate pass
            target.type = ItemType.Equipment;
            target.inventoryCategory = InventoryCategory.Apparel;
            target.usageType = UsageType.Equipment;

            target.basePrice = price;
            target.sortOrder = sortOrder;

            // Per-item half (Armor.as:97-172).
            target.armorHP = armorHP;
            target.armorTip = tip;
            target.armorHideMane = hideMane;
            target.armorIndestructible = indestructible;
            target.armorNoRepair = noRepair;

            // Per-level half (Armor.as:186-193).
            target.armourLevels = (EquipmentData[])levels.Clone();
            target.equipment = levels[0];

            // NOT set, deliberately: requiredLevel. AS3's armour @lvl is the *upgrade ceiling*
            // (Armor.maxlvl), not a character level — the sibling AmmoDataImporter maps its own @lvl
            // to requiredLevel, and reusing that mapping here would be wrong.
        }
    }

    /// <summary>
    /// Parses the <c>&lt;armor&gt;</c> elements of <c>AllData.as</c>. Pure: no Unity types, no I/O,
    /// no RNG.
    ///
    /// <para><b>Why regex and not an XML parser.</b> The elements are embedded in an ActionScript
    /// source file rather than a well-formed document — the same reason
    /// <c>AmmoDataImporter</c>/<c>SimpleDataImporter</c> use the extract-by-hand approach this file
    /// follows. XML comments are stripped first because <c>AllData.as</c> contains 127 of them and one
    /// of them documents the attribute meanings in prose.</para>
    ///
    /// <para><b>Culture.</b> Every number is parsed with <see cref="CultureInfo.InvariantCulture"/>.
    /// The default overloads accept thousands separators, so on a locale where <c>.</c> is a group
    /// separator (<c>de-DE</c>) <c>qual='0.25'</c> parses as <b>25</b> — a silent ×100 error that
    /// would present as "armour absorbs everything". The XML always uses <c>.</c>.</para>
    /// </summary>
    public static class ArmourDataParser
    {
        private static readonly Regex ArmourBlockRegex = new Regex(
            @"<armor\b([^>]*?)>(.*?)</armor>",
            RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex UpdRegex = new Regex(
            @"<upd\b([^>]*?)/?>",
            RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex AttributeRegex = new Regex(
            @"([A-Za-z_][A-Za-z0-9_]*)\s*=\s*['""]([^'""]*)['""]",
            RegexOptions.Compiled);

        private static readonly Regex XmlCommentRegex = new Regex(
            @"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>Attributes of <c>&lt;armor&gt;</c> this parser reads into the definition.</summary>
        private static readonly HashSet<string> HandledArmourAttributes = new HashSet<string>
        {
            "id", "price", "sort", "hp", "tip", "hide", "und", "norep",
        };

        /// <summary>Attributes of <c>&lt;upd&gt;</c> this parser reads into the level stats.</summary>
        private static readonly HashSet<string> HandledUpdAttributes = new HashSet<string>
        {
            "armor", "marmor", "qual", "dexter",
        };

        /// <summary>
        /// <c>&lt;armor&gt;</c> attributes AS3 reads but the port has no consumer for, with the reason.
        /// Not a skip list — an inventory of deliberate omissions, so the next pass can tell a decision
        /// from an oversight.
        /// </summary>
        private static readonly Dictionary<string, string> IgnoredArmourAttributes = new Dictionary<string, string>
        {
            { "clo", "Armor.clo, read only by UnitPlayer.changeArmor() (:3812-3815) to decide whether the " +
                     "armour replaces prevArmor — a visual-layer field, so it belongs with the equip/visual " +
                     "wire, not the stat data" },
            { "lvl", "Armor.maxlvl, the declared *upgrade ceiling* — cross-checked against the <upd> count " +
                     "and warned on, never written to requiredLevel" },
            { "kolcomp", "component quantity for the upgrade — crafting subsystem" },
            { "comp", "component id used for the upgrade — crafting subsystem" },
            { "h2o", "Armor.h2oMult, water-drain multiplier — no consumer in the port" },
            { "tre", "Armor.tre — no consumer in the port" },
            { "melee", "Armor.meleeMult, a damage multiplier applied by Pers.armorParameters() — the port has " +
                       "no equivalent aggregator" },
            { "guns", "Armor.gunsMult — as melee" },
            { "magic", "Armor.magicMult — as melee" },
            { "crit", "Armor.crit, crit chance — same aggregator" },
            { "abil", "armour ability id — the ability subsystem is not ported" },
            { "fly", "Armor.ableFly, flight permission — only 'ali' sets it" },
        };

        /// <summary><c>&lt;upd&gt;</c> counterparts of <see cref="IgnoredArmourAttributes"/>.</summary>
        private static readonly Dictionary<string, string> IgnoredUpdAttributes = new Dictionary<string, string>
        {
            { "kol", "the *component quantity* needed for this upgrade level, read by PipPage.as:429-431 " +
                     "(crafting UI), never by Armor.getXmlParam" },
            { "radx", "Armor.radVul = 1 - radx, consumed by Pers.armorParameters():2031-2035 — the port has " +
                      "no radiation channel" },
            { "sneak", "Armor.sneak, scales Pers.visiMult at :2025-2029 — the port has no sneak channel" },
            { "mana", "Armor.maxmana, the armour's own mana pool" },
            { "act", "Armor.dmana_act, armour-ability mana cost" },
            { "used", "Armor.dmana_use, armour-ability mana cost" },
            { "res", "Armor.dmana_res, armour-ability mana restore" },
            { "dark", "NOT read by Armor.getXmlParam at all — 'amul_adept' carries dark='0.2' and AS3 " +
                      "ignores it. Recorded so the omission is not later 'fixed' into a divergence" },
        };

        /// <summary>
        /// Parse every <c>&lt;armor&gt;</c> element in an <c>AllData.as</c> source.
        /// </summary>
        /// <exception cref="FormatException">A block has no <c>id</c>.</exception>
        public static List<ArmourDefinitionData> ParseAll(string allDataXml)
        {
            var result = new List<ArmourDefinitionData>();
            if (string.IsNullOrEmpty(allDataXml))
                return result;

            string xml = XmlCommentRegex.Replace(allDataXml, string.Empty);

            foreach (Match match in ArmourBlockRegex.Matches(xml))
            {
                string head = match.Groups[1].Value.Trim();
                if (head.EndsWith("/", StringComparison.Ordinal))
                    head = head.Substring(0, head.Length - 1);

                result.Add(ParseBlock(head, match.Groups[2].Value));
            }

            return result;
        }

        /// <summary>
        /// Parse one armour element from its attribute text and its inner XML.
        /// </summary>
        /// <param name="armourAttributes">The attribute text of <c>&lt;armor …&gt;</c>, without the tag.</param>
        /// <param name="innerXml">Everything between <c>&lt;armor …&gt;</c> and <c>&lt;/armor&gt;</c>.</param>
        public static ArmourDefinitionData ParseBlock(string armourAttributes, string innerXml)
        {
            var data = new ArmourDefinitionData();
            var attributes = ReadAttributes(armourAttributes ?? string.Empty);

            if (!attributes.TryGetValue("id", out string id) || string.IsNullOrEmpty(id))
                throw new FormatException($"<armor> element with no id attribute: <armor {armourAttributes}>");

            data.id = id;

            // ── Per-item half (Armor.as:97-172) ──────────────────────────────────────────────────
            data.price = ReadInt(attributes, "price", 0);
            data.sortOrder = ReadInt(attributes, "sort", 0);
            data.armorHP = ReadInt(attributes, "hp", 0);
            data.tip = ReadInt(attributes, "tip", 1);
            data.hideMane = ReadInt(attributes, "hide", 0) != 0;
            data.indestructible = attributes.ContainsKey("und");   // presence, not value
            data.noRepair = attributes.ContainsKey("norep");        // presence, not value

            foreach (var attribute in attributes)
            {
                if (HandledArmourAttributes.Contains(attribute.Key))
                    continue;

                data.warnings.Add(IgnoredArmourAttributes.TryGetValue(attribute.Key, out string reason)
                    ? $"{id}: <armor @{attribute.Key}='{attribute.Value}'> dropped — {reason}"
                    : $"{id}: <armor @{attribute.Key}='{attribute.Value}'> UNRECOGNISED — no port consumer and " +
                      "not on the deliberate-omission list; add it or it is silently lost.");
            }

            // ── Per-level half (Armor.as:186-193) ────────────────────────────────────────────────
            var levels = new List<EquipmentData>();
            foreach (Match upd in UpdRegex.Matches(innerXml ?? string.Empty))
                levels.Add(ParseUpd(data, upd.Groups[1].Value));

            if (levels.Count == 0)
            {
                data.warnings.Add($"{id}: <armor> has no <upd> child — every rating reads as 0, *including* " +
                                  "qual, so this armour would absorb nothing. AS3 would fault on xml.upd[0] here.");
                levels.Add(default);
            }

            data.levels = levels.ToArray();

            // Cross-check the declared ceiling against the actual table. AS3 indexes xml.upd[] with the
            // *constructor argument*, so @lvl is only a declaration — but if the two disagree, one of
            // them is wrong, and the failure mode is a level that silently has no stats.
            if (attributes.TryGetValue("lvl", out string lvlText))
            {
                if (!TryInt(lvlText, out int declaredMax))
                    data.warnings.Add($"{id}: <armor @lvl='{lvlText}'> is not an integer.");
                else if (declaredMax != data.MaxLevel)
                    data.warnings.Add($"{id}: <armor @lvl='{declaredMax}'> but {levels.Count} <upd> children " +
                                      $"(max index {data.MaxLevel}) — one of the two is wrong.");
            }

            data.warnings.Sort(StringComparer.Ordinal);
            return data;
        }

        /// <summary>Parse one <c>&lt;upd&gt;</c> element's attributes into a level's stats.</summary>
        private static EquipmentData ParseUpd(ArmourDefinitionData owner, string updAttributes)
        {
            var equipment = default(EquipmentData);
            var attributes = ReadAttributes(updAttributes);

            foreach (var attribute in attributes)
            {
                switch (attribute.Key)
                {
                    case "armor":
                        if (TryInt(attribute.Value, out int armor)) equipment.armor = armor;
                        else owner.warnings.Add($"{owner.id}: <upd @armor='{attribute.Value}'> is not an integer.");
                        continue;

                    case "marmor":
                        if (TryInt(attribute.Value, out int magicArmor)) equipment.magicArmor = magicArmor;
                        else owner.warnings.Add($"{owner.id}: <upd @marmor='{attribute.Value}'> is not an integer.");
                        continue;

                    case "qual":
                        if (TryFloat(attribute.Value, out float qual)) equipment.reliability = qual;
                        else owner.warnings.Add($"{owner.id}: <upd @qual='{attribute.Value}'> is not a number.");
                        continue;

                    case "dexter":
                        if (TryFloat(attribute.Value, out float dexterity)) equipment.dexterity = dexterity;
                        else owner.warnings.Add($"{owner.id}: <upd @dexter='{attribute.Value}'> is not a number.");
                        continue;
                }

                // The thirteen resistances AS3 parses. ResistTable owns the name mapping so the XML
                // attribute names live in exactly one place.
                if (TryFloat(attribute.Value, out float resist) &&
                    equipment.resists.SetResistByAs3Attribute(attribute.Key, resist))
                    continue;

                owner.warnings.Add(IgnoredUpdAttributes.TryGetValue(attribute.Key, out string reason)
                    ? $"{owner.id}: <upd @{attribute.Key}='{attribute.Value}'> dropped — {reason}"
                    : $"{owner.id}: <upd @{attribute.Key}='{attribute.Value}'> UNRECOGNISED — no port consumer " +
                      "and not on the deliberate-omission list; add it or it is silently lost.");
            }

            return equipment;
        }

        // ── Primitives ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// All <c>name='value'</c> / <c>name="value"</c> pairs in an attribute string.
        /// Last occurrence wins on a duplicate name.
        /// </summary>
        public static Dictionary<string, string> ReadAttributes(string attributeText)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(attributeText))
                return result;

            foreach (Match match in AttributeRegex.Matches(attributeText))
                result[match.Groups[1].Value] = match.Groups[2].Value;

            return result;
        }

        private static int ReadInt(Dictionary<string, string> attributes, string name, int fallback)
            => attributes.TryGetValue(name, out string raw) && TryInt(raw, out int value) ? value : fallback;

        /// <summary>Invariant-culture integer parse — see the culture note on the class.</summary>
        private static bool TryInt(string raw, out int value)
            => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

        /// <summary>
        /// Invariant-culture float parse. <see cref="NumberStyles.Float"/> deliberately excludes
        /// <see cref="NumberStyles.AllowThousands"/>, so <c>"0.25"</c> can never be read as <c>25</c>.
        /// </summary>
        private static bool TryFloat(string raw, out float value)
            => float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
