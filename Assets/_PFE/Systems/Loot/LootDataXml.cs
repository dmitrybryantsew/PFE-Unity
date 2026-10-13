using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;
using PFE.Data.Definitions;

namespace PFE.Systems.Loot
{
    /// <summary>
    /// The loot half of <c>AllData.as</c>, parsed once: the flattened <see cref="LootPoolRow"/> list
    /// <see cref="LootPoolSet.Build"/> consumes, plus the per-id item facts
    /// <see cref="ILootItemCatalog"/> resolves.
    ///
    /// <para><b>Why this is the runtime source and not the imported ScriptableObjects.</b> The pool
    /// builder needs, per row, exactly the attributes <c>LootGen.init()</c> reads — <c>@tip</c>,
    /// <c>@tip2</c>, <c>@chance</c>, <c>@chance2</c>, <c>@stage</c>, <c>@lvl</c>, and for a weapon
    /// <c>com.@stage</c>/<c>@chance</c>/<c>@worth</c>/<c>@uniq</c> plus the presence of the
    /// <c>&lt;com&gt;</c> child. Only <c>legacyTip</c>/<c>tip2</c> are imported onto
    /// <c>ItemDefinition</c> today, and the weapon <c>&lt;com&gt;</c> metadata is imported by nothing —
    /// so a pool built from the definitions would give every item weight 1 and leave the weapon pool
    /// <b>empty</b>. That is a silent divergence, not a smaller version of the truth. This reads the
    /// same oracle rows the tables were transcribed from.</para>
    ///
    /// <para><b>Well-formedness.</b> The block is parsed with <see cref="XElement"/>, the same reader
    /// <see cref="AllDataXml"/> already uses in production. It is safe here because
    /// <c>&lt;item&gt;</c>/<c>&lt;weapon&gt;</c>/<c>&lt;armor&gt;</c> are all top-level rows of
    /// <c>&lt;all&gt;</c>: measured on the shipped file, the occurrence count of <c>&lt;item </c> (500)
    /// equals its line count (500), i.e. no <c>&lt;item&gt;</c> is nested inside an <c>&lt;obj&gt;</c>.
    /// A <c>Descendants</c> scan is therefore exact, not approximate.</para>
    /// </summary>
    public sealed class LootData
    {
        /// <summary>Flattened rows for <see cref="LootPoolSet.Build"/>, in the oracle's order
        /// (weapons, then items — <c>LootGen.as:41</c> then <c>:77</c>).</summary>
        public readonly List<LootPoolRow> Rows;

        /// <summary><c>AllData.d.item</c> by id.</summary>
        public readonly Dictionary<string, LootItemRow> Items;

        /// <summary><c>AllData.d.weapon</c> by id.</summary>
        public readonly Dictionary<string, LootItemRow> Weapons;

        /// <summary><c>AllData.d.armor</c> by id.</summary>
        public readonly Dictionary<string, LootItemRow> Armors;

        internal LootData(List<LootPoolRow> rows,
                          Dictionary<string, LootItemRow> items,
                          Dictionary<string, LootItemRow> weapons,
                          Dictionary<string, LootItemRow> armors)
        {
            Rows = rows;
            Items = items;
            Weapons = weapons;
            Armors = armors;
        }
    }

    /// <summary>
    /// Reads <see cref="LootData"/> out of the AS3 <c>AllData.as</c> source — the single reader shared
    /// by the runtime pool builder and the offline fixtures.
    /// </summary>
    public static class LootDataXml
    {
        /// <summary>Why a parse could not produce data. Returned rather than inferred from an empty
        /// result, because "no block" and "a block with no rows" are different problems.</summary>
        public enum Outcome
        {
            Ok,
            MissingBlock,
            ParseFailed,
        }

        /// <summary>
        /// Parse the embedded <c>&lt;all&gt;</c> literal. <paramref name="error"/> is set on every
        /// non-<see cref="Outcome.Ok"/> return.
        /// </summary>
        public static Outcome TryParse(string allDataText, out LootData data, out string error)
        {
            data = null;
            error = null;

            string block = AllDataXml.ExtractRootBlock(allDataText);
            if (block == null)
            {
                error = $"no {AllDataXml.RootOpen}...{AllDataXml.RootClose} block";
                return Outcome.MissingBlock;
            }

            XElement root;
            try
            {
                root = XElement.Parse(block);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return Outcome.ParseFailed;
            }

            data = Parse(root);
            return Outcome.Ok;
        }

        /// <summary>Parse an already-resolved <c>&lt;all&gt;</c> element.</summary>
        public static LootData Parse(XElement root)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));

            var rows = new List<LootPoolRow>();
            var items = new Dictionary<string, LootItemRow>(StringComparer.Ordinal);
            var weapons = new Dictionary<string, LootItemRow>(StringComparer.Ordinal);
            var armors = new Dictionary<string, LootItemRow>(StringComparer.Ordinal);

            // LootGen.as:41-76 — weapons first, matching the oracle's build order.
            foreach (XElement w in root.Descendants("weapon"))
            {
                string id = Str(w, "id");
                if (string.IsNullOrEmpty(id)) continue;

                int weaponTip = (int)(Int(w, "tip") ?? 0);
                int? level = Int(w, "lvl");
                XElement com = w.Element("com");

                var row = new LootPoolRow
                {
                    Id = id,
                    Kind = LootPoolRowKind.Weapon,
                    WeaponTip = weaponTip,
                    HasCom = com != null,
                    Level = level,
                };

                if (com != null)
                {
                    // LootGen.as:45-63 — stage/chance/worth come off <com>; lvl comes off the weapon.
                    row.Stage = Int(com, "stage");
                    row.Chance = Float(com, "chance");
                    row.Worth = Float(com, "worth");
                    row.UniqChance = Float(com, "uniq");
                }

                rows.Add(row);

                if (!weapons.ContainsKey(id))
                {
                    weapons[id] = new LootItemRow(
                        id, LootTips.Weapon,
                        kol: Int(w, "kol"),
                        limitKey: Str(w, "limit"),
                        limitMultiplier: Float(w, "mlim"),
                        limitAbsolute: Int(w, "maxlim"));
                }
            }

            // LootGen.as:77-113 — items.
            foreach (XElement it in root.Descendants("item"))
            {
                string id = Str(it, "id");
                if (string.IsNullOrEmpty(id)) continue;

                string tip = Str(it, "tip");
                string tip2 = Str(it, "tip2");

                rows.Add(new LootPoolRow
                {
                    Id = id,
                    Kind = LootPoolRowKind.Item,
                    Tip = tip,
                    Tip2 = tip2,
                    Stage = Int(it, "stage"),
                    Chance = Float(it, "chance"),
                    Chance2 = Float(it, "chance2"),
                    Level = Int(it, "lvl"),
                    // LootGen.as:93 — `Boolean(item.sk.length())`: an element count, so any <sk> child.
                    HasSkill = it.Element("sk") != null,
                });

                if (!items.ContainsKey(id))
                {
                    items[id] = new LootItemRow(
                        id, tip,
                        kol: Int(it, "kol"),
                        limitKey: Str(it, "limit"),
                        limitMultiplier: Float(it, "mlim"),
                        limitAbsolute: Int(it, "maxlim"));
                }
            }

            // Armour is in no pool (there is no arr["armor"]) but the catalog must resolve it, because
            // a weapon/armour roll reads @tip and an amulet (armor tip '3') is forced to condition 1.
            foreach (XElement a in root.Descendants("armor"))
            {
                string id = Str(a, "id");
                if (string.IsNullOrEmpty(id) || armors.ContainsKey(id)) continue;

                armors[id] = new LootItemRow(
                    id, Str(a, "tip"),
                    kol: Int(a, "kol"),
                    limitKey: Str(a, "limit"),
                    limitMultiplier: Float(a, "mlim"),
                    limitAbsolute: Int(a, "maxlim"),
                    armorTip: Str(a, "tip"));
            }

            return new LootData(rows, items, weapons, armors);
        }

        // ── attribute helpers ─────────────────────────────────────────────────────────────────────────
        //
        // Absent and present-but-empty are the SAME string here, but they are different for the numeric
        // reads: `Int`/`Float` return null when the attribute is absent, and the pool builder relies on
        // that null to mean "passes the gate" (LootGen.as:138 `_loc8_.st == null`). A present `stage='0'`
        // is 0, not null.

        private static string Str(XElement e, string name)
        {
            XAttribute a = e.Attribute(name);
            return a != null ? a.Value : null;
        }

        private static int? Int(XElement e, string name)
        {
            XAttribute a = e.Attribute(name);
            if (a == null) return null;
            return int.TryParse(a.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                ? v
                : (int?)null;
        }

        private static float? Float(XElement e, string name)
        {
            XAttribute a = e.Attribute(name);
            if (a == null) return null;
            return float.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
                ? v
                : (float?)null;
        }
    }
}
