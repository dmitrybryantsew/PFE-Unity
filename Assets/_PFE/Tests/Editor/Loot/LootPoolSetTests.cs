using System.Collections.Generic;
using NUnit.Framework;
using PFE.Systems.Loot;

namespace PFE.Tests.Editor.Loot
{
    /// <summary>
    /// D5's data half: the port of <c>LootGen.init()</c> (<c>LootGen.as:29-115</c>).
    ///
    /// <para>The rules that are easy to get wrong and are pinned here: a weapon joins the pool only if it
    /// has a <c>&lt;com&gt;</c> child; <c>@chance</c> defaults to <b>1</b> on an item but to <b>0</b> on a
    /// weapon; <c>@chance2</c> falls back to <c>@chance</c> and then to <b>0</b> — not to 1, which is the
    /// asymmetry between <c>LootGen.as:89</c> and <c>:108</c>; and an <i>absent</i> <c>com.@uniq</c>
    /// creates no uniq entry while <c>uniq='0'</c> creates one with weight zero.</para>
    /// </summary>
    [TestFixture]
    public class LootPoolSetTests
    {
        private static LootPoolRow Item(string id, string tip, float? chance = null, int? stage = null,
                                       int? level = null, string tip2 = null, float? chance2 = null,
                                       bool hasSkill = false)
        {
            return new LootPoolRow
            {
                Id = id,
                Kind = LootPoolRowKind.Item,
                Tip = tip,
                Tip2 = tip2,
                Chance = chance,
                Chance2 = chance2,
                Stage = stage,
                Level = level,
                HasSkill = hasSkill,
            };
        }

        private static LootPoolRow Weapon(string id, int weaponTip, bool hasCom = true, float? chance = null,
                                         int? stage = null, int? level = null, float? worth = null,
                                         float? uniq = null)
        {
            return new LootPoolRow
            {
                Id = id,
                Kind = LootPoolRowKind.Weapon,
                WeaponTip = weaponTip,
                HasCom = hasCom,
                Chance = chance,
                Stage = stage,
                Level = level,
                Worth = worth,
                UniqChance = uniq,
            };
        }

        // =====================================================================
        //  The four keys init() creates unconditionally
        // =====================================================================

        /// <summary>
        /// <b><c>init()</c> creates <c>weapon</c>, <c>magic</c>, <c>uniq</c> and <c>pers</c> before it
        /// reads a single row</b> (<c>LootGen.as:37-40</c>). That is observable: <c>arr["uniq"]</c> is a
        /// real — empty — array when no weapon declares a uniq variant, which is why
        /// <c>getRandom("uniq")</c> returns <c>null</c> at <c>:151</c> rather than at <c>:124</c>.
        /// </summary>
        [Test]
        public void Build_CreatesTheFourAlwaysPresentKeys()
        {
            var set = LootPoolSet.Build(new List<LootPoolRow>());

            Assert.IsTrue(set.HasPoolKey(LootPoolSet.WeaponKey));
            Assert.IsTrue(set.HasPoolKey(LootPoolSet.MagicKey));
            Assert.IsTrue(set.HasPoolKey(LootPoolSet.UniqKey));
            Assert.IsTrue(set.HasPoolKey(LootPoolSet.PersKey));
            Assert.AreEqual(0, set.PersIds.Count);
        }

        /// <summary>A tip with no pool is <c>null</c>, matching <c>arr[tip] == null</c>
        /// (<c>LootGen.as:124</c>).</summary>
        [Test]
        public void GetPool_UnknownKey_IsNull()
        {
            var set = LootPoolSet.Build(new List<LootPoolRow>());
            Assert.IsNull(set.GetPool("no-such-tip"));
            Assert.IsNull(set.GetPool(null));
        }

        // =====================================================================
        //  The weapon pool
        // =====================================================================

        /// <summary>
        /// <b>A weapon joins <c>arr["weapon"]</c> only when it has a <c>&lt;com&gt;</c> child.</b>
        /// <c>init()</c> guards on <c>weap.com.length() != 0</c> (<c>LootGen.as:43</c>) — an element count,
        /// so there is no E4X ambiguity here. The consequence is real: a weapon with no <c>&lt;com&gt;</c>
        /// is in no pool and can never be rolled.
        /// </summary>
        [Test]
        public void Build_WeaponPool_RequiresAComChild()
        {
            var rows = new List<LootPoolRow>
            {
                Weapon("withcom", weaponTip: 1, hasCom: true, chance: 2f),
                Weapon("nocom", weaponTip: 1, hasCom: false, chance: 5f),
            };

            var pool = LootPoolSet.Build(rows).GetPool(LootPoolSet.WeaponKey);

            Assert.AreEqual(1, pool.Count);
            Assert.AreEqual("withcom", pool[0].Id);
        }

        /// <summary>
        /// <b>Only tips 1..3 reach <c>arr["weapon"]</c>.</b> <c>init()</c> filters
        /// <c>@tip &gt; 0 &amp;&amp; @tip &lt; 4</c> (<c>LootGen.as:41</c>). In <c>AllData</c> that excludes
        /// 60 tip-0 and 25 tip-4 weapons, several of which carry a <c>&lt;com&gt;</c> child.
        /// </summary>
        [Test]
        public void Build_WeaponPool_OnlyTipsOneToThree()
        {
            var rows = new List<LootPoolRow>
            {
                Weapon("tip0", weaponTip: 0, chance: 1f),
                Weapon("tip1", weaponTip: 1, chance: 1f),
                Weapon("tip2", weaponTip: 2, chance: 1f),
                Weapon("tip3", weaponTip: 3, chance: 1f),
                Weapon("tip4", weaponTip: 4, chance: 1f),
                Weapon("tip5", weaponTip: 5, chance: 1f),
            };

            var set = LootPoolSet.Build(rows);

            CollectionAssert.AreEquivalent(new[] { "tip1", "tip2", "tip3" }, Ids(set.GetPool(LootPoolSet.WeaponKey)));
            CollectionAssert.AreEqual(new[] { "tip5" }, Ids(set.GetPool(LootPoolSet.MagicKey)));
        }

        /// <summary>
        /// <b><c>arr["magic"]</c> is filled with literal zero weights and never reads <c>&lt;com&gt;</c></b>
        /// (<c>LootGen.as:66-76</c>). So the magic pool is inert: with two or more entries its total weight
        /// is 0 and <c>getRandom</c> returns <c>null</c>. Reproduced, not repaired — the oracle's only
        /// reader is the debug "give everything" pass.
        /// </summary>
        [Test]
        public void Build_MagicPool_UsesTipFiveAndIgnoresCom()
        {
            var rows = new List<LootPoolRow>
            {
                Weapon("m_nocom", weaponTip: 5, hasCom: false, chance: 9f),
                Weapon("m_com", weaponTip: 5, hasCom: true, chance: 9f, worth: 4f),
            };

            var magic = LootPoolSet.Build(rows).GetPool(LootPoolSet.MagicKey);

            CollectionAssert.AreEquivalent(new[] { "m_nocom", "m_com" }, Ids(magic));
            foreach (var e in magic)
            {
                Assert.AreEqual(0f, e.Chance, "the magic pool is authored with chance 0 (LootGen.as:71)");
            }
        }

        /// <summary>
        /// <b>An absent <c>com.@uniq</c> creates no uniq entry; <c>uniq='0'</c> creates one with weight
        /// 0.</b> The distinction is <c>if(weap.com.@uniq.length())</c> (<c>LootGen.as:53</c>) — a
        /// <i>presence</i> test. 45 of the 129 <c>&lt;com&gt;</c> weapons in <c>AllData</c> carry exactly
        /// <c>uniq='0'</c>, so this is not a corner case.
        /// </summary>
        [Test]
        public void Build_UniqPool_AbsentUniqMakesNoEntry_ZeroUniqMakesOne()
        {
            var rows = new List<LootPoolRow>
            {
                Weapon("no_uniq", weaponTip: 1, chance: 1f, uniq: null),
                Weapon("zero_uniq", weaponTip: 1, chance: 1f, uniq: 0f),
                Weapon("has_uniq", weaponTip: 1, chance: 1f, uniq: 0.5f),
            };

            var uniq = LootPoolSet.Build(rows).GetPool(LootPoolSet.UniqKey);

            CollectionAssert.AreEquivalent(new[] { "zero_uniq^1", "has_uniq^1" }, Ids(uniq));
            Assert.AreEqual(0f, Find(uniq, "zero_uniq^1").Chance);
            Assert.AreEqual(0.5f, Find(uniq, "has_uniq^1").Chance);
        }

        // =====================================================================
        //  The item pools, and the chance defaults that differ
        // =====================================================================

        /// <summary>
        /// <b>An item's <c>@chance</c> defaults to 1, a weapon's to 0.</b> <c>LootGen.as:89</c> writes
        /// <c>(item.@chance.length() ? item.@chance : 1)</c> while <c>:48</c> writes
        /// <c>weap.com.@chance</c> with no fallback — so an unweighted weapon contributes nothing to the
        /// running total. 389 of 500 items carry <c>@chance</c>, so the default is the common path for the
        /// other 111.
        /// </summary>
        [Test]
        public void Build_ChanceDefaults_AreOneForItemsAndZeroForWeapons()
        {
            var rows = new List<LootPoolRow>
            {
                Item("i_default", "food", chance: null),
                Item("i_explicit", "food", chance: 0.4f),
                Weapon("w_default", weaponTip: 1, chance: null),
            };

            var set = LootPoolSet.Build(rows);

            Assert.AreEqual(1f, Find(set.GetPool("food"), "i_default").Chance,
                "an absent item @chance is 1 (LootGen.as:89)");
            Assert.AreEqual(0.4f, Find(set.GetPool("food"), "i_explicit").Chance);
            Assert.AreEqual(0f, Find(set.GetPool(LootPoolSet.WeaponKey), "w_default").Chance,
                "an absent weapon com.@chance is 0 (LootGen.as:48)");
        }

        /// <summary>
        /// <b><c>@tip2</c> builds a second pool for the same row</b>, weighted by <c>@chance2</c> falling
        /// back to <c>@chance</c> and then to <b>0</b> (<c>LootGen.as:108</c>). The final fallback is the
        /// asymmetry with <c>:89</c>: a tip2 row with neither attribute contributes weight 0, where the
        /// same row in its primary pool would have contributed 1.
        /// </summary>
        [Test]
        public void Build_Tip2Chance_FallsBackToChanceThenZero()
        {
            var rows = new List<LootPoolRow>
            {
                Item("t2_own", "food", tip2: "co", chance: 3f, chance2: 0.5f),
                Item("t2_inherit", "food", tip2: "co", chance: 2f, chance2: null),
                Item("t2_neither", "food", tip2: "co", chance: null, chance2: null),
            };

            var co = LootPoolSet.Build(rows).GetPool("co");

            CollectionAssert.AreEquivalent(new[] { "t2_own", "t2_inherit", "t2_neither" }, Ids(co));
            Assert.AreEqual(0.5f, Find(co, "t2_own").Chance, "@chance2 wins when present");
            Assert.AreEqual(2f, Find(co, "t2_inherit").Chance, "otherwise @chance is inherited");
            Assert.AreEqual(0f, Find(co, "t2_neither").Chance, "and with neither, 0 — not 1");
        }

        /// <summary>
        /// <b><c>arr["pers"]</c> collects art, impl and any item with a <c>&lt;sk&gt;</c> child</b>
        /// (<c>LootGen.as:93-96</c>), and it is a list of <b>ids</b>, not weighted entries. The oracle
        /// pushes unconditionally, so duplicates survive; the port must not de-duplicate.
        /// </summary>
        [Test]
        public void Build_PersIds_IncludeArtImplAndSkillRows()
        {
            var rows = new List<LootPoolRow>
            {
                Item("art_x", LootTips.Art),
                Item("impl_x", LootTips.Impl),
                Item("skill_x", "food", hasSkill: true),
                Item("plain_x", "food"),
            };

            var set = LootPoolSet.Build(rows);

            CollectionAssert.AreEquivalent(new[] { "art_x", "impl_x", "skill_x" }, set.PersIds);
        }

        /// <summary>
        /// <b>Row order is preserved.</b> <c>getRandom</c> returns the first entry whose running total
        /// exceeds the roll (<c>LootGen.as:160-166</c>), so the order of <c>arr</c> decides which item a
        /// given random number selects. Re-sorting the pool would change every roll in the game.
        /// </summary>
        [Test]
        public void Build_PreservesRowOrder()
        {
            var rows = new List<LootPoolRow>
            {
                Item("first", "food", chance: 1f),
                Item("second", "food", chance: 1f),
                Item("third", "food", chance: 1f),
            };

            CollectionAssert.AreEqual(new[] { "first", "second", "third" },
                                      Ids(LootPoolSet.Build(rows).GetPool("food")));
        }

        /// <summary>
        /// <b>A weapon that carries a <c>&lt;com&gt;</c> but sits outside tips 1..3 joins neither pool.</b>
        /// 24 such weapons exist in <c>AllData</c>; the tip-5 ones reappear in <c>magic</c>, the tip-0 ones
        /// vanish entirely.
        /// </summary>
        [Test]
        public void Build_WeaponWithComButOutOfRangeTip_JoinsNoWeightedPool()
        {
            var rows = new List<LootPoolRow>
            {
                Weapon("tip0_com", weaponTip: 0, hasCom: true, chance: 1f, uniq: 1f),
            };

            var set = LootPoolSet.Build(rows);

            Assert.AreEqual(0, set.GetPool(LootPoolSet.WeaponKey).Count);
            Assert.AreEqual(0, set.GetPool(LootPoolSet.UniqKey).Count);
            Assert.AreEqual(0, set.GetPool(LootPoolSet.MagicKey).Count);
        }

        /// <summary>
        /// <b>A row with no <c>@tip</c> at all is skipped.</b> <c>init()</c> guards on
        /// <c>item.@tip.length()</c> (<c>LootGen.as:79</c>); no <c>AllData</c> row hits this, but a
        /// hand-authored or half-imported row would, and it must not create an <c>arr[""]</c>.
        /// </summary>
        [Test]
        public void Build_RowWithNoTip_CreatesNoPool()
        {
            var rows = new List<LootPoolRow> { Item("orphan", tip: string.Empty) };
            var set = LootPoolSet.Build(rows);

            Assert.IsFalse(set.HasPoolKey(string.Empty));
            Assert.AreEqual(4, set.Keys.Count, "only the four always-created keys");
        }

        // ── helpers ───────────────────────────────────────────────────────────

        private static List<string> Ids(IReadOnlyList<LootPoolEntry> pool)
        {
            var ids = new List<string>();
            if (pool == null) return ids;
            for (int i = 0; i < pool.Count; i++) ids.Add(pool[i].Id);
            return ids;
        }

        private static LootPoolEntry Find(IReadOnlyList<LootPoolEntry> pool, string id)
        {
            for (int i = 0; i < pool.Count; i++)
            {
                if (pool[i].Id == id) return pool[i];
            }
            Assert.Fail("no pool entry with id '" + id + "'");
            return default;
        }
    }
}
