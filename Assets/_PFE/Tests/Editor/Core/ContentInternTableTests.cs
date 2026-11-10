using System.Collections.Generic;
using NUnit.Framework;
using PFE.Sim.Content;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// The interned, content-versioned id table behind <c>SimUnitState.DefinitionId</c> and
    /// <c>SimProjectileState.DefinitionId</c>.
    ///
    /// <para><b>What is actually at risk.</b> Not "does a lookup work" — that is trivial. The risk is
    /// that two hosts build <i>different</i> tables from the same content and every key then means
    /// something else: a plasma round renders and damages as a pistol, on one machine, intermittently.
    /// So the assertions here are about the properties that make two builds agree — order independence,
    /// ordinal ordering, and duplicate handling — plus the reserved key that makes "not interned"
    /// expressible at all.</para>
    /// </summary>
    [TestFixture]
    public class ContentInternTableTests
    {
        private static readonly string[] Weapons = { "sniper", "pistol", "plasma", "acidgr", "molotov" };

        /// <summary>
        /// The core property: registration order must not matter. The content registry's iteration order
        /// is a dictionary's, and mods load in whatever order the manifest gives.
        /// </summary>
        [Test]
        public void Build_IsOrderIndependent()
        {
            ContentInternTable forward = ContentInternTable.Build(Weapons);

            var reversed = new List<string>(Weapons);
            reversed.Reverse();
            ContentInternTable backward = ContentInternTable.Build(reversed);

            var shuffled = new List<string> { "plasma", "molotov", "pistol", "acidgr", "sniper" };
            ContentInternTable jumbled = ContentInternTable.Build(shuffled);

            Assert.IsTrue(forward.SameContentAs(backward),
                "Two hosts registering the same weapons in opposite orders must build the same table.");
            Assert.IsTrue(forward.SameContentAs(jumbled));

            for (int i = 0; i < Weapons.Length; i++)
            {
                Assert.AreEqual(forward.GetKeyOrDefault(Weapons[i]), backward.GetKeyOrDefault(Weapons[i]),
                    "Weapon '" + Weapons[i] + "' must intern to the same key regardless of input order.");
            }
        }

        /// <summary>
        /// Key 0 is reserved for "not interned". Without it a missing definition is indistinguishable
        /// from the first definition in the table — which is exactly the failure a hash scheme cannot
        /// avoid.
        /// </summary>
        [Test]
        public void Build_ReservesKeyZeroAndReportsUninterned()
        {
            ContentInternTable table = ContentInternTable.Build(Weapons);

            Assert.AreEqual(Weapons.Length, table.Count);
            Assert.AreEqual(ContentInternTable.Uninterned, table.GetKeyOrDefault("not-a-weapon"));
            Assert.AreEqual(ContentInternTable.Uninterned, table.GetKeyOrDefault(null));
            Assert.AreEqual(ContentInternTable.Uninterned, table.GetKeyOrDefault(string.Empty));
            Assert.IsNull(table.GetContentId(ContentInternTable.Uninterned),
                "Key 0 must not name a definition.");

            // ── Pinned by VALUE, not by symbol ──────────────────────────────────────────────
            //
            // Every assertion above compares against ContentInternTable.Uninterned, so none of them can
            // detect a change to that constant — a mutant that set Uninterned = 1 passed all of them.
            // That is the presence-only defect in its purest form: a test that reads the symbol it is
            // supposed to be pinning.
            //
            // 0 is a WIRE contract, not an implementation detail: a default-constructed
            // SimProjectileState has DefinitionId 0, and every "no definition" record in a payload
            // relies on 0 meaning exactly that. So the value is asserted literally here.
            Assert.AreEqual(0, (int)ContentInternTable.Uninterned,
                "The reserved key must be 0: a default-constructed record carries DefinitionId 0, and "
                + "changing this constant silently reinterprets every such record on the wire.");

            Assert.IsNull(table.GetContentId(0), "Key 0 must never name a definition.");

            foreach (string id in Weapons)
            {
                Assert.AreNotEqual(ContentInternTable.Uninterned, table.GetKeyOrDefault(id),
                    "'" + id + "' must get a real key, never the reserved one.");
            }
        }

        /// <summary>
        /// Duplicates and blanks are dropped rather than counted. If they were counted, a registry that
        /// yielded one id twice would shift every later key and the two hosts would disagree.
        /// </summary>
        [Test]
        public void Build_DropsDuplicatesNullsAndBlanks()
        {
            ContentInternTable clean = ContentInternTable.Build(new[] { "a", "b", "c" });
            ContentInternTable messy = ContentInternTable.Build(
                new[] { "a", null, "", "b", "a", "c", "b", "" });

            Assert.AreEqual(3, messy.Count);
            Assert.IsTrue(clean.SameContentAs(messy),
                "Noise in the source list must not shift a key.");

            Assert.AreEqual(clean.GetKeyOrDefault("c"), messy.GetKeyOrDefault("c"));
        }

        /// <summary>
        /// <b>Ordinal, not culture ordering.</b> <c>"ä"</c> (U+00E4) sorts after <c>"z"</c> byte-wise but
        /// next to <c>"a"</c> under most cultures, so this pair is the discriminator: a
        /// culture-sensitive sort would produce a different table on a machine with a different locale
        /// and every key would silently mean something else.
        /// </summary>
        [Test]
        public void Build_OrdersByOrdinalNotCulture()
        {
            ContentInternTable table = ContentInternTable.Build(new[] { "z", "\u00e4" });

            Assert.Less(table.GetKeyOrDefault("z"), table.GetKeyOrDefault("\u00e4"),
                "Ordinal compares UTF-16 code units: 'z' (122) before U+00E4 (228). A culture-aware "
                + "sort would put the umlaut first and break host/client agreement.");
        }

        /// <summary>Round trip: a key resolves back to the id that produced it.</summary>
        [Test]
        public void GetContentId_RoundTripsEveryKey()
        {
            ContentInternTable table = ContentInternTable.Build(Weapons);

            for (ushort key = 1; key <= table.Count; key++)
            {
                string id = table.GetContentId(key);
                Assert.IsNotNull(id, "Key " + key + " must name a definition.");
                Assert.AreEqual(key, table.GetKeyOrDefault(id), "Key " + key + " must round-trip.");
            }

            Assert.IsNull(table.GetContentId(unchecked((ushort)(table.Count + 1))),
                "An out-of-range key must answer null, not throw inside a per-tick loop.");
        }

        /// <summary>
        /// Two tables with the same ids in a different order are the same table; two with different ids
        /// are not. The check a peer handshake needs.
        /// </summary>
        [Test]
        public void SameContentAs_DetectsADifferentContentSet()
        {
            ContentInternTable a = ContentInternTable.Build(new[] { "a", "b" });
            ContentInternTable b = ContentInternTable.Build(new[] { "a", "b" });
            ContentInternTable c = ContentInternTable.Build(new[] { "a", "c" });
            ContentInternTable d = ContentInternTable.Build(new[] { "a" });

            Assert.IsTrue(a.SameContentAs(b));
            Assert.IsFalse(a.SameContentAs(c), "A renamed definition is a different content set.");
            Assert.IsFalse(a.SameContentAs(d), "A missing definition is a different content set.");
            Assert.IsFalse(a.SameContentAs(null));
        }

        /// <summary>
        /// The ushort space is finite and the overflow must be loud. Wrapping would give key 1 to two
        /// different definitions, which is the collision the whole class exists to avoid — just a
        /// deterministic one, which is worse because it would reproduce everywhere.
        /// </summary>
        [Test]
        public void Build_ThrowsRatherThanWrappingAtTheKeySpaceLimit()
        {
            var ids = new List<string>(ushort.MaxValue + 1);
            for (int i = 0; i <= ushort.MaxValue; i++) ids.Add("def" + i);

            Assert.Throws<System.InvalidOperationException>(() => ContentInternTable.Build(ids),
                "65 536 definitions would wrap onto key 0; refusing is the only honest answer.");

            // One fewer must be fine, so the boundary is pinned from both sides.
            ids.RemoveAt(ids.Count - 1);
            Assert.AreEqual(ushort.MaxValue, ContentInternTable.Build(ids).Count);
        }

        /// <summary>The empty table is usable, not null — a containerless build still has to answer.</summary>
        [Test]
        public void Empty_AnswersUninternedForEverything()
        {
            ContentInternTable empty = ContentInternTable.Empty;

            Assert.AreEqual(0, empty.Count);
            Assert.AreEqual(ContentInternTable.Uninterned, empty.GetKeyOrDefault("anything"));
            Assert.IsNull(empty.GetContentId(0));
            Assert.IsTrue(empty.SameContentAs(ContentInternTable.Build(new string[0])));
        }
    }
}
