using System;
using System.Collections.Generic;

namespace PFE.Systems.Loot
{
    /// <summary>
    /// A faithful port of <c>LootGen.init()</c> (<c>LootGen.as:29-115</c>) — the global <c>arr</c> of
    /// candidate pools that every table roll draws from.
    ///
    /// <para><b>One pool set, not one per table.</b> AS3 builds a single static <c>arr</c> once and every
    /// branch of <c>lootCont</c>/<c>lootDrop</c> reads it by tip. So the data half of the loot system is
    /// <i>this</i> object — a tip-keyed pool set derived from the item and weapon rows — and the logic
    /// half is the per-key branch code in <see cref="LootRoller"/>. There is deliberately no
    /// per-table asset: a table is an <c>if/else if</c> chain containing loops, perk gates, biome gates
    /// and side effects, which a serialised entry list cannot hold.</para>
    ///
    /// <para><b>Pool keys produced</b> (<c>LootGen.as:37-40</c> always creates the first four, even when
    /// they end up empty): <c>weapon</c>, <c>magic</c>, <c>uniq</c>, <c>pers</c>, plus one key per
    /// distinct <c>&lt;item @tip&gt;</c> and <c>&lt;item @tip2&gt;</c> value actually present.</para>
    ///
    /// <para><b><c>pers</c> is a list of ids, not a weighted pool</b> (<c>LootGen.as:95</c> pushes
    /// <c>item.@id</c> into a plain array). It is exposed as <see cref="PersIds"/> and is read by the
    /// perk-container path, not by any loot table.</para>
    ///
    /// <para><b><c>magic</c> is inert.</b> It is filled from every <c>tip == 5</c> weapon with a literal
    /// <c>chance: 0</c> (<c>LootGen.as:66-76</c>). With more than one entry the cumulative total is 0, so
    /// <c>getRandom</c>'s scan finds nothing and returns <c>null</c>; with exactly one entry the
    /// <c>length == 1</c> short-circuit returns it. No loot table asks for <c>magic</c> — its only reader
    /// is the debug "give everything" pass (<c>Invent.as:1635</c>). Reproduced, not "fixed".</para>
    /// </summary>
    public sealed class LootPoolSet : ILootPoolProvider
    {
        public const string WeaponKey = "weapon";
        public const string MagicKey = "magic";
        public const string UniqKey = "uniq";
        public const string PersKey = "pers";

        private readonly Dictionary<string, List<LootPoolEntry>> _pools;
        private readonly List<string> _persIds;
        private readonly List<string> _keysInCreationOrder;

        private LootPoolSet(Dictionary<string, List<LootPoolEntry>> pools, List<string> persIds,
                            List<string> keysInCreationOrder)
        {
            _pools = pools;
            _persIds = persIds;
            _keysInCreationOrder = keysInCreationOrder;
        }

        /// <summary>
        /// Every tip that has a pool, in creation order (<c>weapon</c>, <c>magic</c>, <c>uniq</c>,
        /// <c>pers</c>, then each tip/tip2 in the order first seen). Exposed for diagnostics and for a
        /// test to assert the four always-created keys are present even when empty.
        /// </summary>
        public IReadOnlyList<string> Keys => _keysInCreationOrder;

        /// <summary>AS3 <c>arr["pers"]</c> — ids of items that are <c>art</c>, <c>impl</c>, or carry a
        /// <c>&lt;sk&gt;</c> child. Order preserved; duplicates are possible and are not collapsed
        /// (<c>init()</c> pushes unconditionally).</summary>
        public IReadOnlyList<string> PersIds => _persIds;

        /// <summary>
        /// AS3 <c>arr[tip]</c>. Returns <c>null</c> for a tip with no pool at all — the same thing
        /// <c>getRandom</c> checks (<c>LootGen.as:124</c>). A tip whose pool exists but is empty is
        /// returned as an empty list; <c>getRandom</c> then fails at <c>_loc5_.length == 0</c>
        /// (<c>:151</c>), which is the same outcome by a different branch.
        /// </summary>
        public IReadOnlyList<LootPoolEntry> GetPool(string tip)
        {
            if (tip == null) return null;
            return _pools.TryGetValue(tip, out var list) ? (IReadOnlyList<LootPoolEntry>)list : null;
        }

        /// <summary>True when the tip has a pool entry list (possibly empty).</summary>
        public bool HasPoolKey(string tip) => tip != null && _pools.ContainsKey(tip);

        /// <summary>
        /// Build the pool set from flattened rows, preserving row order — order is load-bearing because
        /// <c>getRandom</c> scans the pool in order and returns the first entry whose running total
        /// exceeds the roll (<c>LootGen.as:160-166</c>).
        /// </summary>
        public static LootPoolSet Build(IReadOnlyList<LootPoolRow> rows)
        {
            var pools = new Dictionary<string, List<LootPoolEntry>>(StringComparer.Ordinal);
            var persIds = new List<string>();
            var order = new List<string>();

            // LootGen.as:37-40 — created unconditionally, before any row is read.
            CreatePool(pools, order, WeaponKey);
            CreatePool(pools, order, MagicKey);
            CreatePool(pools, order, UniqKey);
            CreatePool(pools, order, PersKey);

            if (rows == null) return new LootPoolSet(pools, persIds, order);

            foreach (var row in rows)
            {
                if (row == null || string.IsNullOrEmpty(row.Id)) continue;

                if (row.Kind == LootPoolRowKind.Weapon)
                {
                    // LootGen.as:41-65 — weapons with tip 1..3 that carry a <com> child.
                    if (row.WeaponTip > 0 && row.WeaponTip < 4 && row.HasCom)
                    {
                        Add(pools, order, WeaponKey,
                            new LootPoolEntry(row.Id, row.Chance ?? 0f, row.Stage, row.Level, row.Worth));

                        // :53 `if(weap.com.@uniq.length())`. An absent attribute makes no entry;
                        // `uniq='0'` DOES make one, with weight 0.
                        if (row.UniqChance.HasValue)
                        {
                            Add(pools, order, UniqKey,
                                new LootPoolEntry(row.Id + "^1", row.UniqChance.Value, row.Stage, row.Level,
                                                  row.Worth));
                        }
                    }

                    // :66-76 — tip 5 weapons, literal zero weights, no <com> read at all.
                    if (row.WeaponTip == 5)
                    {
                        Add(pools, order, MagicKey, new LootPoolEntry(row.Id, 0f, 0, 0, 0f));
                    }

                    continue;
                }

                // LootGen.as:79-97 — the primary tip pool.
                if (!string.IsNullOrEmpty(row.Tip))
                {
                    // :81-85 — the pool is created on first sight; `r` starts at 0 per tip.
                    CreatePool(pools, order, row.Tip);
                    // :89 — `@chance` absent means 1, not 0.
                    Add(pools, order, row.Tip,
                        new LootPoolEntry(row.Id, row.Chance ?? 1f, row.Stage, row.Level, null));

                    // :93 — art, impl, or any row with a <sk> child joins arr["pers"].
                    if (row.Tip == LootTips.Art || row.Tip == LootTips.Impl || row.HasSkill)
                        persIds.Add(row.Id);
                }

                // LootGen.as:98-112 — the second pool, keyed by @tip2.
                if (!string.IsNullOrEmpty(row.Tip2))
                {
                    CreatePool(pools, order, row.Tip2);
                    // :108 — `@chance2` absent falls back to `@chance`; and if THAT is absent the E4X
                    // value is an empty XMLList, whose Number() is 0 — so 0, not 1. Different rule from
                    // the primary pool's `?? 1f`, and the reason this line is not symmetric with :89.
                    float chance2 = row.Chance2 ?? row.Chance ?? 0f;
                    Add(pools, order, row.Tip2,
                        new LootPoolEntry(row.Id, chance2, row.Stage, row.Level, null));
                }
            }

            return new LootPoolSet(pools, persIds, order);
        }

        private static void CreatePool(Dictionary<string, List<LootPoolEntry>> pools, List<string> order,
                                       string key)
        {
            if (pools.ContainsKey(key)) return;
            pools[key] = new List<LootPoolEntry>();
            order.Add(key);
        }

        private static void Add(Dictionary<string, List<LootPoolEntry>> pools, List<string> order, string key,
                                LootPoolEntry entry)
        {
            if (!pools.TryGetValue(key, out var list))
            {
                list = new List<LootPoolEntry>();
                pools[key] = list;
                order.Add(key);
            }
            list.Add(entry);
        }
    }
}
