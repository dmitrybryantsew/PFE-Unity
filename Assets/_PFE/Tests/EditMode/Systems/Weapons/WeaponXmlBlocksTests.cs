using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using PFE.Systems.Items;
using PFE.Systems.Weapons;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins <see cref="WeaponXmlBlocks"/> — the matcher that splits <c>AllData.as</c> into
    /// <c>&lt;weapon&gt;</c> blocks.
    ///
    /// <para><b>Why this fixture exists.</b> The importer used a pattern that required a literal
    /// closing tag — <c>&lt;weapon\s+id='([^']+)'([^&gt;]*)&gt;(.*?)&lt;/weapon&gt;</c> — so it could
    /// not see a self-closing block. Nine weapons are written that way, and they are the <b>last</b>
    /// weapon entries in <c>AllData.as</c> (lines 4017-4025), which means there is no later
    /// <c>&lt;/weapon&gt;</c> anywhere for the pattern to close against: the match simply never
    /// happens. Measured on the real file, the old pattern found 204 blocks and the new one 213,
    /// which is exactly the number of literal <c>&lt;weapon id='</c> occurrences.</para>
    ///
    /// <para><b>Why nothing caught it.</b> It was not a crash and not a missing asset — the nine
    /// <c>.asset</c> files already existed from an earlier run, so the import reported success and
    /// left them frozen (mtimes 2026-09-29 against 2026-10-03 for the other 204). Nothing in the
    /// project could see it, because the pattern lived in <c>PFE.Editor</c>, which <c>PFE.Tests</c>
    /// does not reference. That is the reason the matcher was moved into the runtime assembly and
    /// these tests exist: a guard in the editor assembly would have been invisible to the wall.</para>
    ///
    /// <para>Every test below pairs what must be found with what must not — a matcher that returned
    /// every block twice, or swallowed a body into the next block, would otherwise pass.</para>
    /// </summary>
    [TestFixture]
    public class WeaponXmlBlocksTests
    {
        // ── Fixtures ──────────────────────────────────────────────────────────

        /// <summary>
        /// The verbatim tail of <c>AllData.as</c>: the nine self-closing spell weapons, preceded by
        /// their <c>&lt;item&gt;</c> definitions and followed by the next section. Reproduced rather
        /// than simplified because the bug depends on the exact shape — self-closing blocks with no
        /// later closing tag anywhere in the source.
        /// </summary>
        private const string SpellTail =
            "\t\t\t<item id='sp_invulner' tip='spell' us='1' price='4000' atk='1' dam='50' " +
            "magic='800' mana='200' snd='mshit' culd='12' mess='spell'/>\n" +
            "\t\t\t<weapon id='sp_slow' tip='5' skill='6' perslvl='3' spell='1'/>\n" +
            "\t\t\t<weapon id='sp_mwall' tip='5' skill='6' perslvl='6' spell='1'/>\n" +
            "\t\t\t<weapon id='sp_blast' tip='5' skill='7' perslvl='9' spell='1'/>\n" +
            "\t\t\t<weapon id='sp_cryst' tip='5' skill='6' perslvl='12' spell='1'/>\n" +
            "\t\t\t<weapon id='sp_kdash' tip='5' skill='7' perslvl='15' spell='1'/>\n" +
            "\t\t\t<weapon id='sp_mshit' tip='5' skill='6' perslvl='18' spell='1'/>\n" +
            "\t\t\t<weapon id='sp_moon' tip='5' skill='6' perslvl='20' spell='1'/>\n" +
            "\t\t\t<weapon id='sp_gwall' tip='5' skill='6' perslvl='21' spell='1'/>\n" +
            "\t\t\t<weapon id='sp_invulner' tip='5' skill='6' perslvl='24' spell='1'/>\n" +
            "\n" +
            "\t\t\t<!--       \u0426\u0435\u043d\u043d\u044b\u0435 \u0432\u0435\u0449\u0438        -->\n" +
            "\t\t\t<item id='bit' tip='valuables' sell='1' fall='fall_caps' fc='2'/>\n";

        /// <summary>The nine ids, in file order, as AS3 spells them.</summary>
        private static readonly string[] NineSpellIds =
        {
            "sp_slow", "sp_mwall", "sp_blast", "sp_cryst", "sp_kdash",
            "sp_mshit", "sp_moon", "sp_gwall", "sp_invulner",
        };

        /// <summary>A closed block with a real body, abbreviated from <c>p10mm</c> in AllData.as.</summary>
        private const string ClosedBlock =
            "<weapon id='p10mm' tip='2' cat='2' skill='2' lvl='1' alicorn='1'>\n" +
            "\t<char maxhp='300' damage='11' rapid='7' prec='8' knock='4' destroy='10'/>\n" +
            "\t<phis speed='150' deviation='5' recoil='5' massa='2' phisbul='1'/>\n" +
            "\t<ammo holder='12' reload='35' rashod='1' mana='50' magic='100' recharg='0'/>\n" +
            "</weapon>";

        // ── The regression ────────────────────────────────────────────────────

        /// <summary>
        /// The bug itself: a self-closing block at the end of the source, with no later
        /// <c>&lt;/weapon&gt;</c>, must still be matched.
        /// </summary>
        [Test]
        public void SelfClosingBlockWithNoLaterClosingTag_IsMatched()
        {
            var blocks = WeaponXmlBlocks.Parse(SpellTail);

            var ids = blocks.Select(b => b.Id).ToList();

            Assert.AreEqual(9, blocks.Count,
                "the nine self-closing spell weapons must all be matched; the old pattern found 0 " +
                "because there is no </weapon> after them to close against.");

            CollectionAssert.AreEqual(NineSpellIds, ids, "file order must be preserved");

            // Absent control — the <item> definitions share the ids and must not be picked up.
            Assert.IsFalse(ids.Contains("bit"), "the following <item> must not be matched");
            Assert.IsFalse(ids.Contains("sp_invulner_item"), "no synthetic id may appear");
        }

        /// <summary>
        /// A self-closing block must report itself as such, with an empty body. Reporting a
        /// body that is really the next block's text is the failure mode a greedy pattern has.
        /// </summary>
        [Test]
        public void SelfClosingBlock_HasEmptyBodyAndIsFlagged()
        {
            var slow = WeaponXmlBlocks.Parse(SpellTail).Single(b => b.Id == "sp_slow");

            Assert.IsTrue(slow.SelfClosing, "sp_slow is written <weapon …/> and must be flagged self-closing");
            Assert.AreEqual("", slow.Body, "a self-closing block has no body — not the next block's text");
        }

        /// <summary>
        /// The root attributes must survive intact, including the two this change is about —
        /// <c>skill</c> (which the importer maps to <c>skillLevel</c>) and <c>spell</c>.
        /// </summary>
        [Test]
        public void SelfClosingBlock_PreservesItsRootAttributes()
        {
            var slow = WeaponXmlBlocks.Parse(SpellTail).Single(b => b.Id == "sp_slow");

            // Exact equality rather than a set of Contains calls, because Contains is easy to fool
            // here: "perslvl='3'" itself contains "lvl='", so an absent-control written as
            // DoesNotContain("lvl='") fails against a perfectly correct parse. The whole attribute
            // run is short and known, so assert all of it.
            Assert.AreEqual(" tip='5' skill='6' perslvl='3' spell='1'", slow.RootAttrs);

            // Absent control: an attribute no spell weapon carries, and that no other attribute
            // name contains as a substring.
            StringAssert.DoesNotContain("throwtip", slow.RootAttrs);
        }

        // ── No regression on the 204 closed blocks ────────────────────────────

        /// <summary>
        /// A block with a body must still be matched, must not be flagged self-closing, and must
        /// keep its body byte-for-byte — the fix must not cost the existing 204 anything.
        /// </summary>
        [Test]
        public void ClosedBlock_KeepsItsBodyAndIsNotFlaggedSelfClosing()
        {
            var blocks = WeaponXmlBlocks.Parse(ClosedBlock);

            Assert.AreEqual(1, blocks.Count);
            var p10mm = blocks[0];

            Assert.AreEqual("p10mm", p10mm.Id);
            Assert.IsFalse(p10mm.SelfClosing, "a block with a closing tag is not self-closing");
            StringAssert.Contains("<char maxhp='300' damage='11'", p10mm.Body);
            StringAssert.Contains("<ammo holder='12' reload='35'", p10mm.Body);
            // The fixture must really be a closed block, or the assertion above proves nothing.
            StringAssert.Contains("</weapon>", ClosedBlock);
            StringAssert.DoesNotContain("</weapon>", p10mm.Body, "the body must stop at the closing tag");
        }

        /// <summary>
        /// Closed and self-closing blocks mixed in one source: every weapon appears exactly once,
        /// and the count equals the number of literal <c>&lt;weapon id='</c> occurrences.
        /// </summary>
        [Test]
        public void MixedSource_MatchesEveryWeaponExactlyOnce()
        {
            string source = ClosedBlock + "\n" + SpellTail;

            var blocks = WeaponXmlBlocks.Parse(source);
            var ids    = blocks.Select(b => b.Id).ToList();

            Assert.AreEqual(10, blocks.Count, "1 closed + 9 self-closing");
            Assert.AreEqual(10, ids.Distinct().Count(), "no block may be matched twice");

            // Independent ground truth: count the opening tags in the source itself.
            int rawTags = CountOccurrences(source, "<weapon id='");
            Assert.AreEqual(rawTags, blocks.Count,
                "the matcher must find exactly as many blocks as there are <weapon id=' tags");

            Assert.AreEqual("p10mm", ids[0], "the closed block comes first");
            CollectionAssert.AreEqual(NineSpellIds, ids.Skip(1).ToList());
        }

        /// <summary>
        /// Two self-closing blocks back to back must not consume each other.
        /// </summary>
        [Test]
        public void AdjacentSelfClosingBlocks_DoNotSwallowEachOther()
        {
            string source = "<weapon id='aa' tip='5' spell='1'/>\n<weapon id='bb' tip='5' spell='1'/>";

            var blocks = WeaponXmlBlocks.Parse(source);

            Assert.AreEqual(2, blocks.Count);
            Assert.AreEqual("aa", blocks[0].Id);
            Assert.AreEqual("bb", blocks[1].Id);
            Assert.AreEqual("", blocks[0].Body);
            Assert.AreEqual("", blocks[1].Body);
        }

        // ── Edge cases ────────────────────────────────────────────────────────

        /// <summary>No weapons at all — must be an empty list, not null and not an exception.</summary>
        [Test]
        public void SourceWithNoWeapons_ReturnsEmptyList()
        {
            var blocks = WeaponXmlBlocks.Parse("<item id='bit' tip='valuables'/>");

            Assert.IsNotNull(blocks);
            Assert.AreEqual(0, blocks.Count);
        }

        /// <summary>Null and empty sources must behave like "no weapons".</summary>
        [Test]
        public void NullOrEmptySource_ReturnsEmptyList()
        {
            Assert.AreEqual(0, WeaponXmlBlocks.Parse(null).Count);
            Assert.AreEqual(0, WeaponXmlBlocks.Parse("").Count);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Counts non-overlapping occurrences of <paramref name="needle"/> in
        /// <paramref name="haystack"/>. Used as an independent ground truth for the block count so
        /// the assertion does not simply restate the matcher's own answer.
        /// </summary>
        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, at = 0;
            while ((at = haystack.IndexOf(needle, at, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                at += needle.Length;
            }
            return count;
        }
    }

    /// <summary>
    /// Pins <see cref="WeaponXmlAttrs"/> — the attribute readers the weapon importer uses.
    ///
    /// <para><b>Why this fixture exists.</b> The readers matched an attribute name with no boundary:
    /// <c>Regex.Match(src, name + "='([^']*)'")</c>. A name that is the <i>suffix</i> of another name
    /// therefore matched the longer one whenever the longer one was written first. Three pairs collide
    /// in the shipped <c>AllData.as</c>, and the consequences were large and entirely silent:</para>
    ///
    /// <list type="bullet">
    /// <item><description><c>expl</c> inside <c>damexpl</c> — <b>55 weapons</b>. <c>balemine</c>'s
    /// <c>&lt;char … damexpl='750' … expl='300'/&gt;</c> gave <c>explRadius</c> <b>750</b> instead of
    /// <b>300</b>.</description></item>
    /// <item><description><c>kol</c> inside <c>dkol</c> — <b>13 weapons</b>. <c>dronlaser</c> has
    /// <c>dkol='15'</c> and no <c>kol</c>, so <c>projectilesPerShot</c> read <b>15</b> instead of the
    /// fallback <b>1</b>.</description></item>
    /// <item><description><c>lvl</c> inside <c>perslvl</c> — <b>24 weapons</b>. <c>fireball</c> has
    /// <c>perslvl='12'</c> and no <c>lvl</c>, so <c>weaponLevel</c> read <b>12</b> instead of <b>0</b> —
    /// and that value feeds the <c>checkAvail</c> skill gate.</description></item>
    /// </list>
    ///
    /// <para>Measured: 81 reads across 72 of the 213 weapons changed when the boundary was added, and
    /// exactly those three (node, attribute) pairs were involved — 27 non-colliding reads were
    /// byte-identical on all 213, which is what proves the boundary does not over-reach.</para>
    ///
    /// <para>The fixtures below are the <b>verbatim</b> attribute strings from <c>AllData.as</c>, not
    /// invented ones — the bug depends on the real attribute order, so a tidy synthetic string would
    /// have hidden it. That is exactly how it survived this long.</para>
    /// </summary>
    [TestFixture]
    public class WeaponXmlAttrsTests
    {
        // ── Verbatim AllData.as attribute strings ─────────────────────────────

        /// <summary>`balemine`'s tier-1 &lt;char&gt;: `damexpl` is written BEFORE `expl`.</summary>
        private const string BalemineChar =
            " rapid='30' maxhp='10' time='15' massafix='200' sens='100' damexpl='750' " +
            "tipdam='15' knock='25' destroy='5000' expl='300'";

        /// <summary>`dronlaser`'s tier-1 &lt;char&gt;: `dkol` present, `kol` absent.</summary>
        private const string DronlaserChar =
            " damage='3' pier='50' dkol='15' rapid='1' prec='15' crit='0' prep='10' " +
            "tipdam='5' destroy='10'";

        /// <summary>`fireball`'s weapon root: `perslvl` present, `lvl` absent.</summary>
        private const string FireballRoot = " tip='5' cat='6' skill='6' perslvl='12'";

        /// <summary>`fireball`'s tier-1 &lt;char&gt;: `damexpl` is written BEFORE `expl`.</summary>
        private const string FireballChar =
            " damexpl='85' damage='0' rapid='24' prec='10' crit='0' tipdam='3' knock='25' " +
            "destroy='120' expl='100'";

        /// <summary>
        /// The <c>.32 round</c> <c>&lt;item&gt;</c> row, verbatim from <c>AllData.as:3763</c> — the row
        /// whose hover threw out of the Ammunition tab. It carries <b>fractional</b> <c>sell</c>,
        /// <c>chance</c> and <c>price</c> next to an integral <c>kol</c>, so one fixture exercises both
        /// the fractional and the integral read.
        /// </summary>
        private const string P32AmmoRow =
            "<item base='p32' id='p32' tip='a' kol='5' chance='1.2' stage='1' lvl='0' price='2.5' sell='0.5' m='1'/>";

        // ── The three real collisions ─────────────────────────────────────────

        [Test]
        public void Expl_DoesNotReadDamexpl()
        {
            // balemine: the wrong read gave 750 (the blast damage) as the blast RADIUS.
            Assert.AreEqual(300f, WeaponXmlAttrs.AttrF(BalemineChar, "expl", 0f), 0.001f,
                "char@expl is 300; 750 is char@damexpl and must not be picked up");

            // The longer name must still read its own value.
            Assert.AreEqual(750f, WeaponXmlAttrs.AttrF(BalemineChar, "damexpl", 0f), 0.001f,
                "char@damexpl is 750 and must be unaffected by the boundary");
        }

        [Test]
        public void Kol_DoesNotReadDkol_AndFallsBackWhenAbsent()
        {
            // dronlaser has no `kol` at all: the wrong read returned dkol's 15, i.e. fifteen
            // projectiles per shot instead of one.
            Assert.AreEqual(1, WeaponXmlAttrs.AttrI(DronlaserChar, "kol", 1),
                "char@kol is absent on dronlaser — the fallback 1 applies, not dkol's 15");

            Assert.AreEqual(15, WeaponXmlAttrs.AttrI(DronlaserChar, "dkol", 0),
                "char@dkol is 15 and must be unaffected by the boundary");
        }

        [Test]
        public void Lvl_DoesNotReadPerslvl_AndFallsBackWhenAbsent()
        {
            // fireball has no `lvl`. Reading perslvl's 12 as weaponLevel made the checkAvail skill
            // gate refuse a weapon the owner may use.
            Assert.AreEqual(0, WeaponXmlAttrs.AttrI(FireballRoot, "lvl", 0),
                "weapon@lvl is absent on fireball — the fallback 0 applies, not perslvl's 12");

            Assert.AreEqual(12, WeaponXmlAttrs.AttrI(FireballRoot, "perslvl", 0),
                "weapon@perslvl is 12 and must be unaffected by the boundary");

            Assert.AreEqual(6, WeaponXmlAttrs.AttrI(FireballRoot, "skill", 0),
                "weapon@skill is 6 — a positive control that ordinary reads still work");
        }

        [Test]
        public void FireballChar_ExplIs100NotDamexpl85()
        {
            Assert.AreEqual(100f, WeaponXmlAttrs.AttrF(FireballChar, "expl", 0f), 0.001f);
            Assert.AreEqual(85f,  WeaponXmlAttrs.AttrF(FireballChar, "damexpl", 0f), 0.001f);
        }

        // ── Order independence ────────────────────────────────────────────────

        /// <summary>
        /// The result must not depend on which of the two names is written first. The old reader
        /// returned 300 in one order and 750 in the other, which is why this is asserted both ways
        /// rather than once.
        /// </summary>
        [Test]
        public void CollidingNames_AreOrderIndependent()
        {
            Assert.AreEqual("300", WeaponXmlAttrs.Attr(" expl='300' damexpl='750'", "expl"),
                "expl first");
            Assert.AreEqual("300", WeaponXmlAttrs.Attr(" damexpl='750' expl='300'", "expl"),
                "damexpl first — the order that used to break");

            Assert.AreEqual("750", WeaponXmlAttrs.Attr(" expl='300' damexpl='750'", "damexpl"));
            Assert.AreEqual("750", WeaponXmlAttrs.Attr(" damexpl='750' expl='300'", "damexpl"));
        }

        /// <summary>
        /// The same trap on <see cref="WeaponXmlAttrs.AttrBool"/>, which had no collision in the
        /// shipped data only by luck: no weapon carries an attribute whose name ends in
        /// <c>shell</c>/<c>bumc</c>/<c>radio</c>/<c>alicorn</c>. Pinned so the next such name cannot
        /// slip through.
        /// </summary>
        [Test]
        public void AttrBool_DoesNotReadASuffixedName()
        {
            Assert.IsTrue(WeaponXmlAttrs.AttrBool(" shell='1'", "shell"));
            Assert.IsFalse(WeaponXmlAttrs.AttrBool(" xshell='1'", "shell"),
                "a name ending in 'shell' must not satisfy a read of 'shell'");
            Assert.IsFalse(WeaponXmlAttrs.AttrBool(" shell='0'", "shell"), "value must be '1'");
            Assert.IsFalse(WeaponXmlAttrs.AttrBool("", "shell"), "absent is false");
            Assert.IsFalse(WeaponXmlAttrs.AttrBool(null, "shell"), "null is false, not a throw");
        }

        // ── Positive controls: ordinary reads still work ───────────────────────

        [Test]
        public void OrdinaryReads_StillWork()
        {
            Assert.AreEqual("5", WeaponXmlAttrs.Attr(" tip='5' skill='6'", "tip"));
            Assert.AreEqual("6", WeaponXmlAttrs.Attr(" tip='5' skill='6'", "skill"));
            Assert.AreEqual("5", WeaponXmlAttrs.Attr(" tip='5'", "tip"), "single attribute");
            Assert.AreEqual(5,   WeaponXmlAttrs.AttrI(" lvl='5'", "lvl", 0), "a real lvl still reads");
            Assert.IsTrue(WeaponXmlAttrs.AttrBool(" spell='1'", "spell"));
            Assert.AreEqual("", WeaponXmlAttrs.Attr(" tip='5'", "absent"), "absent with no fallback");
            Assert.AreEqual("X", WeaponXmlAttrs.Attr("", "tip", "X"), "empty source -> fallback");
            Assert.AreEqual("X", WeaponXmlAttrs.Attr(null, "tip", "X"), "null source -> fallback");
        }

        /// <summary>
        /// <b>The Ammunition tab's crash.</b> <c>AttrI</c> used to be a bare
        /// <c>int.Parse</c>, so a <i>fractional</i> attribute value threw
        /// <c>FormatException: Input string was not in a correct format</c> straight out of
        /// <c>PipPageInvView.BuildAmmoInfoText</c> — measured live on <c>p32</c>, the <c>.32 round</c>,
        /// whose row carries <c>sell='0.5'</c>. The exception escaped the pane's builder, so the
        /// Ammunition tab's info pane never filled at all.
        ///
        /// <para><b>Why fractional <c>sell</c> is not an edge case.</b> All 44 ammunition rows lead
        /// with <c>base=</c>, and 26 of them carry a fractional <c>sell</c> (<c>0.1</c>…<c>1.8</c>).
        /// The reader that must survive them is the same one the whole ammo page uses.</para>
        ///
        /// <para><b>Both halves are load bearing, and this is the whole reason the fixture pairs the
        /// two readers.</b> AS3's <c>int(x)</c> coerces through <c>Number</c> and truncates toward
        /// zero, so <c>int('0.5')</c> is <c>0</c> and is <i>not</i> an error — that is
        /// <see cref="WeaponXmlAttrs.AttrI"/>. But the pane's own <c>sell</c> line is gated on an AS3
        /// <c>@sell &gt; 0</c>, which is a <b>Number</b> comparison
        /// (<c>PipPage.as:744-747</c>) — that is <see cref="WeaponXmlAttrs.AttrF"/>, and it must still
        /// see <c>0.5</c>. A guard that only asserted "does not throw" would pass with <c>AttrI</c>
        /// answering its fallback for every value, and a guard that used <c>AttrI</c> for the gate
        /// would silently drop the sell line from every ammo row.</para>
        ///
        /// <para><b>The attributes are extracted the way the caller extracts them.</b> The string under
        /// test is <c>ItemXmlRow.Attrs</c> off <see cref="ItemXmlRows.Parse"/> — the very string
        /// <c>PipPageInvView.ItemRowAttrs</c> hands to these readers — rather than a hand-written
        /// convenience string, so a change in the extraction shape cannot leave this fixture passing
        /// against a shape the view never sees.</para>
        /// </summary>
        [Test]
        public void AttrI_DoesNotThrowOnAFractionalValue_AndAttrFKeepsItPositive()
        {
            List<ItemXmlRow> rows = ItemXmlRows.Parse(P32AmmoRow);
            Assert.AreEqual(1, rows.Count, "one <item> row in, one out");
            string attrs = rows[0].Attrs;

            // The integral read is the positive control: the truncating fallback must not have turned
            // every read into the fallback.
            Assert.AreEqual(5, WeaponXmlAttrs.AttrI(attrs, "kol", 0),
                "kol='5' is integral and must read as 5");

            Assert.AreEqual(0, WeaponXmlAttrs.AttrI(attrs, "sell", 0),
                "sell='0.5' truncates to 0 — it must not throw");
            Assert.AreEqual(1, WeaponXmlAttrs.AttrI(attrs, "chance", 0), "chance='1.2' truncates to 1");
            Assert.AreEqual(2, WeaponXmlAttrs.AttrI(attrs, "price", 0), "price='2.5' truncates to 2");

            // The discriminating pair: the same attribute, two readers, two different answers — and
            // it is AttrF's that the pane's `@sell > 0` line needs.
            Assert.AreEqual(0.5f, WeaponXmlAttrs.AttrF(attrs, "sell", 0f), 0.0001f,
                "the ammo pane's sell line is a NUMBER comparison, so it must still see 0.5");
            Assert.AreNotEqual(0f, WeaponXmlAttrs.AttrF(attrs, "sell", 0f),
                "the two readers must DISAGREE on '0.5' — AttrI truncates to 0 and AttrF keeps it " +
                "positive. If they ever agree, one of them is answering the other's question");

            // Truncation is toward zero, which is what AS3's int() does — not floor.
            Assert.AreEqual(-1, WeaponXmlAttrs.AttrI(" v='-1.7'", "v", 0), "int(-1.7) is -1, not -2");

            // A value that is not a number at all answers the fallback rather than throwing: the real
            // `<mat>` rows carry `m='А'`, a Cyrillic index letter.
            Assert.AreEqual(0, WeaponXmlAttrs.AttrI(" m='А'", "m", 0),
                "a Cyrillic index letter is not a number");
            Assert.AreEqual(7, WeaponXmlAttrs.AttrI(" m='А'", "m", 7),
                "and the caller's own fallback is what comes back");
        }

        /// <summary>
        /// AllData.as writes decimals with <c>.</c> and no value in the file contains a comma (checked:
        /// 0 matches for <c>='[0-9]*,[0-9]*'</c>). Parsing under the ambient culture would make every
        /// float depend on the machine's locale — and under a comma-decimal culture
        /// <c>float.Parse("1.5")</c> returns <b>15</b>, silently.
        /// </summary>
        [Test]
        public void FloatParsing_IsCultureInvariant()
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("ru-RU");   // ',' is the decimal separator
                Assert.AreEqual(1.5f, WeaponXmlAttrs.AttrF(" mana='1.5'", "mana", 0f), 0.0001f,
                    "mana='1.5' must parse as 1.5 under a comma-decimal culture too");
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        /// <summary>
        /// <see cref="WeaponXmlAttrs.AttrPresent"/> answers a different question from
        /// <see cref="WeaponXmlAttrs.AttrBool"/>, and the difference is not cosmetic: AS3's
        /// <c>Boolean(node.@x.length())</c> is true for <b>any</b> one-character value, so
        /// <c>atk='0'</c> is <i>present</i>. The spell flags (<c>prod</c>, <c>tele</c>, <c>atk</c> —
        /// <c>Spell.as:116-126</c>) are read that way and their values are never compared.
        /// </summary>
        [Test]
        public void AttrPresent_IsTrueForAnyValueIncludingZero()
        {
            Assert.IsTrue (WeaponXmlAttrs.AttrPresent(" atk='1'", "atk"), "a normal flag");
            Assert.IsTrue (WeaponXmlAttrs.AttrPresent(" atk='0'", "atk"),
                "AS3 reads @atk.length(), so '0' is PRESENT — AttrBool would say false here");
            Assert.IsTrue (WeaponXmlAttrs.AttrPresent(" atk=''", "atk"),
                "even an empty value has a length() in AS3 terms: the attribute exists");
            Assert.IsFalse(WeaponXmlAttrs.AttrPresent(" tip='spell'", "atk"), "absent");
            Assert.IsFalse(WeaponXmlAttrs.AttrPresent("", "atk"), "empty source");
            Assert.IsFalse(WeaponXmlAttrs.AttrPresent(null, "atk"), "null source");

            // The two readers must disagree on '0' — otherwise one of them is redundant and this
            // fixture is not pinning anything.
            Assert.IsTrue (WeaponXmlAttrs.AttrPresent(" atk='0'", "atk"));
            Assert.IsFalse(WeaponXmlAttrs.AttrBool   (" atk='0'", "atk"));

            // And they must agree on the boundary rule: `datk` is not `atk`.
            Assert.IsFalse(WeaponXmlAttrs.AttrPresent(" datk='1'", "atk"),
                "a name that merely ENDS in the attribute name must not match");
        }
    }

    /// <summary>
    /// Pins <see cref="SpellItemXml"/> — the reader that lifts a spell's twelve attributes off an
    /// <c>&lt;item tip='spell' …&gt;</c> row (<c>Spell.as:83-131</c>).
    ///
    /// <para><b>Why this fixture exists.</b> All nine spell rows carried real data in
    /// <c>AllData.as</c> and none of it reached the port. <c>ItemDefinition</c> had no home for a
    /// spell, so <c>tip='spell'</c> fell into <c>FixDataImport</c>'s <c>default:</c> Misc bucket and
    /// every asset stayed an empty stub (<c>basePrice: 10</c>, <c>displayName: Item Name</c>) — the
    /// same "reported success, wrote nothing" shape as the self-closing-<c>&lt;weapon&gt;</c> bug this
    /// file already guards. The reader lives in the runtime assembly precisely so these tests can run
    /// without Unity: attribute-name-to-field mapping is where the <c>lvl</c>⊂<c>perslvl</c> family of
    /// silent-wrong-value bugs lives.</para>
    ///
    /// <para><b>The rows below are the real ones</b>, copied from <c>AllData.as:4008-4016</c>, in the
    /// shape the importer actually passes — group 1 + group 3 of its regex, i.e. the tag content with
    /// <c>tip='spell'</c> removed. That is deliberate: a hand-written convenience string would not
    /// catch a mismatch in the shape the importer really produces.</para>
    /// </summary>
    [TestFixture]
    public class SpellItemXmlTests
    {
        // ── The nine real rows, verbatim minus `tip='spell'` ──────────────────

        private const string SpSlow =
            "id='sp_slow'  us='1' price='2000' hp='60' magic='500' mana='30' culd='10' rad='200' snd='slow' mess='spell'";
        private const string SpMwall =
            "id='sp_mwall'  us='1' price='1000' atk='1' hp='200' magic='300' culd='20' mana='30' dist='300' line='1' snd='mwall' mess='spell'";
        private const string SpBlast =
            "id='sp_blast'  us='1' price='2500' atk='1' dam='20' magic='300' culd='10' mana='30' rad='500' tele='1' snd='blast' mess='spell'";
        private const string SpCryst =
            "id='sp_cryst'  us='1' price='2000' atk='1' prod='1' magic='50' culd='0' mana='5' snd='crystal' mess='spell'";
        private const string SpKdash =
            "id='sp_kdash'  us='1' price='2000' dam='20' magic='100' mana='10' culd='3' tele='1' snd='dash' mess='spell'";
        private const string SpMshit =
            "id='sp_mshit'  us='1' price='2000' atk='1' hp='150' magic='500' mana='100' culd='30' snd='mshit' mess='spell'";
        private const string SpMoon =
            "id='sp_moon'  us='1' price='3000' atk='1' magic='800' mana='300' snd='crystal' culd='60' mess='spell' pet_info='moon'";
        private const string SpGwall =
            "id='sp_gwall'  us='1' price='3000' atk='1' magic='500' mana='50' hp='100' dam='10' dist='300' line='1' snd='mwall' culd='6' mess='spell'";
        private const string SpInvulner =
            "id='sp_invulner'  us='1' price='4000' atk='1' dam='50' magic='800' mana='200' snd='mshit' culd='12' mess='spell'";

        [Test]
        public void RealRows_ReadTheOracleValues()
        {
            var slow = SpellItemXml.Read(SpSlow);
            Assert.AreEqual(60f,  slow.hp,    0.001f);
            Assert.AreEqual(500f, slow.magic, 0.001f);
            Assert.AreEqual(30f,  slow.mana,  0.001f);
            Assert.AreEqual(10f,  slow.culd,  0.001f);
            Assert.AreEqual(200f, slow.rad,   0.001f);
            Assert.AreEqual("slow", slow.snd);
            Assert.IsFalse(slow.atk,  "sp_slow has no atk attribute — it is not offensive");
            Assert.IsFalse(slow.line, "and no line attribute");
            Assert.IsFalse(slow.prod);
            Assert.IsFalse(slow.tele);

            var blast = SpellItemXml.Read(SpBlast);
            Assert.AreEqual(20f,  blast.dam,   0.001f);
            Assert.AreEqual(500f, blast.rad,   0.001f);
            Assert.AreEqual(300f, blast.magic, 0.001f);
            Assert.IsTrue(blast.atk,  "sp_blast is offensive");
            Assert.IsTrue(blast.tele, "…and telekinetic");
            Assert.IsFalse(blast.line, "but it has no line attribute");
            Assert.AreEqual(0f, blast.hp, 0.001f, "sp_blast declares no hp");

            var gwall = SpellItemXml.Read(SpGwall);
            Assert.AreEqual(100f, gwall.hp,   0.001f);
            Assert.AreEqual(10f,  gwall.dam,  0.001f);
            Assert.AreEqual(300f, gwall.dist, 0.001f);
            Assert.IsTrue(gwall.line, "sp_gwall requires line of sight");

            var cryst = SpellItemXml.Read(SpCryst);
            Assert.AreEqual(50f, cryst.magic, 0.001f);
            Assert.AreEqual(0f,  cryst.culd,  0.001f, "sp_cryst's culd really is 0");
            Assert.IsTrue(cryst.prod, "sp_cryst is the only prod spell");
            Assert.IsFalse(cryst.tele);

            var kdash = SpellItemXml.Read(SpKdash);
            Assert.AreEqual(20f, kdash.dam, 0.001f);
            Assert.IsTrue(kdash.tele);
            Assert.IsFalse(kdash.atk, "sp_kdash is one of the two non-offensive spells");

            Assert.AreEqual(800f, SpellItemXml.Read(SpMoon).magic,   0.001f);
            Assert.AreEqual(150f, SpellItemXml.Read(SpMshit).hp,     0.001f);
            Assert.AreEqual(50f,  SpellItemXml.Read(SpInvulner).dam, 0.001f);
            Assert.AreEqual(300f, SpellItemXml.Read(SpMwall).dist,   0.001f);
            Assert.IsTrue(SpellItemXml.Read(SpMwall).line);
        }

        [Test]
        public void CollidingAttributeNames_DoNotLeakIntoTheSpellFields()
        {
            // The load-bearing test in this fixture. `damexpl`⊃`dam` is a REAL collision in the shipped
            // weapon data (balemine's `<char … damexpl='750' … expl='300'/>`, and 55 weapons share the
            // pair); the rest are the same shape — a longer name ending in the short one — written in
            // the order that breaks an unbounded match, i.e. the longer name FIRST. Without the
            // (?:^|\s) boundary each read below returns the decoy.
            Assert.AreEqual(20f, SpellItemXml.Read(" damexpl='750' dam='20'").dam,
                "`damexpl` is not `dam` — this pair is real, and an unbounded match reads 750");
            Assert.AreEqual(5f,  SpellItemXml.Read(" dmana='7' mana='5'").mana,
                "`dmana` is not `mana`");
            Assert.AreEqual(4f,  SpellItemXml.Read(" shithp='9' hp='4'").hp,
                "`shithp` is not `hp`");
            Assert.AreEqual(6f,  SpellItemXml.Read(" dmagic='7' magic='6'").magic,
                "`dmagic` is not `magic`");
            Assert.AreEqual(300f, SpellItemXml.Read(" antidist='9' dist='300'").dist);

            // The flags use the same boundary, so a name ending in the flag name must not set it.
            Assert.IsFalse(SpellItemXml.Read(" datk='1'").atk, "`datk` is not `atk`");
            Assert.IsFalse(SpellItemXml.Read(" teleport='1'").tele, "`teleport` is not `tele`");
            Assert.IsFalse(SpellItemXml.Read(" product='1'").prod, "`product` is not `prod`");
            Assert.IsFalse(SpellItemXml.Read(" outline='1'").line, "`outline` is not `line`");

            // …and the same names still read correctly when the decoy comes second, which rules out
            // "the fix was to match only the last occurrence".
            Assert.AreEqual(5f, SpellItemXml.Read(" mana='5' dmana='7'").mana);
            Assert.AreEqual(4f, SpellItemXml.Read(" hp='4' shithp='9'").hp);
        }

        [Test]
        public void Culr_IsTheRawAttribute_NotFrameConverted()
        {
            // AS3 converts at CONSTRUCTION: `this.culd = @culd * World.fps` (Spell.as:98), so the
            // attribute is in seconds and the object's field is in frames. The importer must store the
            // raw value — baking the ×30 in would make the conversion invisible and double-apply the
            // moment a caller did it again.
            Assert.AreEqual(10f, SpellItemXml.Read(" culd='10'").culd, 0.001f,
                "must be 10, not 300");
            Assert.AreEqual(0f, SpellItemXml.Read(" culd='0'").culd, 0.001f);
        }

        [Test]
        public void AbsentAttributes_LeaveTheDefaults()
        {
            var empty = SpellItemXml.Read("id='sp_nothing'");
            Assert.AreEqual(0f, empty.hp, 0.001f);
            Assert.AreEqual(0f, empty.magic, 0.001f);
            Assert.AreEqual(0f, empty.culd, 0.001f);
            Assert.IsFalse(empty.line);
            Assert.IsFalse(empty.prod);
            Assert.IsFalse(empty.tele);
            Assert.IsFalse(empty.atk);
            Assert.IsNull(empty.snd);

            // Null and empty sources must behave the same — the importer can hand either.
            Assert.IsFalse(SpellItemXml.Read(null).IsPopulated);
            Assert.IsFalse(SpellItemXml.Read("").IsPopulated);
        }

        [Test]
        public void Snd_DistinguishesAbsentFromPresentButEmpty()
        {
            // Pins the one place SpellItemXml deviates from Attr's `""` default, so a later
            // "simplification" back to `Attr(src, "snd")` cannot pass silently. The oracle makes the
            // distinction: Spell.as:69 leaves `snd` null when never assigned, while :129's
            // `if(this.xml.@snd.length())` is 1 — true — for a present-but-empty `snd=''`, which then
            // assigns "". Both are falsy at :286 (`if(this.snd)`), so the consumer is unaffected;
            // what is at stake is that "never imported" and "imported as empty" stay distinguishable.
            Assert.IsNull(SpellItemXml.Read("id='sp_nothing'").snd, "absent -> null");
            Assert.AreEqual("", SpellItemXml.Read(" snd=''").snd, "present-but-empty -> \"\"");
            Assert.AreEqual("slow", SpellItemXml.Read(" snd='slow'").snd);
        }

        [Test]
        public void IsPopulated_DistinguishesRealDataFromAnEmptyStub()
        {
            // The discriminator the importer's repair branch depends on: the nine pre-existing sp_*
            // assets are non-null but empty, so "did this row import" cannot be answered by null-ness.
            Assert.IsFalse(SpellItemXml.Read("id='sp_slow'").IsPopulated,
                "an id alone is exactly the pre-import stub state");

            // A row with only a sound still counts: `snd` is the one attribute all nine carry.
            Assert.IsTrue(SpellItemXml.Read(" snd='slow'").IsPopulated);
            // As does one with only a cost — sp_cryst has culd='0', so culd alone is not enough.
            Assert.IsTrue(SpellItemXml.Read(" magic='50'").IsPopulated);

            // Every real row must read as populated, or the repair branch would rewrite them forever.
            foreach (var row in new[] { SpSlow, SpMwall, SpBlast, SpCryst, SpKdash,
                                        SpMshit, SpMoon, SpGwall, SpInvulner })
            {
                Assert.IsTrue(SpellItemXml.Read(row).IsPopulated, row);
            }
        }
    }

    /// <summary>
    /// Pins <see cref="WeaponVariantXml"/> — how the unique ("II") variant's index-1 override is read
    /// and folded over the base node.
    ///
    /// <para><b>Why this fixture exists.</b> The merge is presence-gated (<c>Weapon.as:777-861</c>), so
    /// getting it wrong is <i>silent</i>: an override that should have inherited <c>kol</c> from the base
    /// node instead reads 0, the weapon still fires, and nothing goes red. The rule is also the one part
    /// of the feature that must not be duplicated, so it lives in the runtime assembly precisely so this
    /// fixture can reach it — <c>PFE.Tests</c> does not reference <c>PFE.Editor</c>.</para>
    ///
    /// <para><b>Every positive is paired with an absent control.</b> "The override wins" is asserted
    /// beside "an omitted attribute is kept" and "an explicit <c>'0'</c> is an override, not an
    /// inheritance", because a merge that simply returned the override would satisfy the first alone.</para>
    /// </summary>
    [TestFixture]
    public class WeaponVariantXmlTests
    {
        // ── MergeRuns: the load-bearing partial merge ────────────────────────

        [Test]
        public void MergeRuns_PartialOverride_KeepsEveryAttributeTheOverrideOmits()
        {
            // The real `oldshot` pair, verbatim from AllData.as:2796-2797. Tier 1 raises hp 150->250 and
            // damage 6->7 and says NOTHING about kol, knock or destroy — which is exactly why the merge
            // has to exist: reading the override node directly would report kol 0 and knock 0 for a
            // shotgun that fires five pellets with knockback 3.
            const string tier0 = " maxhp='150' damage='6' kol='5' rapid='10' prec='6' knock='3' destroy='10'";
            const string tier1 = " maxhp='250' damage='7' rapid='10' prec='7'";

            string merged = WeaponVariantXml.MergeRuns(tier0, tier1);

            Assert.That(WeaponXmlAttrs.AttrF(merged, "maxhp",   0f), Is.EqualTo(250f).Within(0.001f), "hp");
            Assert.That(WeaponXmlAttrs.AttrF(merged, "damage",  0f), Is.EqualTo(7f).Within(0.001f),   "damage");
            Assert.That(WeaponXmlAttrs.AttrF(merged, "prec",    0f), Is.EqualTo(7f).Within(0.001f),   "prec");

            // The inherited three — the half a naive "take the override" merge loses.
            Assert.That(WeaponXmlAttrs.AttrI(merged, "kol",     0),  Is.EqualTo(5),  "kol inherited");
            Assert.That(WeaponXmlAttrs.AttrF(merged, "knock",   0f), Is.EqualTo(3f).Within(0.001f),
                        "knock inherited");
            Assert.That(WeaponXmlAttrs.AttrF(merged, "destroy", 0f), Is.EqualTo(10f).Within(0.001f),
                        "destroy inherited");
        }

        [Test]
        public void MergeRuns_ExplicitZero_IsAnOverrideAndNotAnInheritance()
        {
            // The whole reason "absent" and "'0'" cannot share a representation. AS3 assigns only when
            // `@attr.length()` — a present `knock='0'` DOES assign, and means "no knockback".
            string merged = WeaponVariantXml.MergeRuns(" knock='3' rapid='10'", " knock='0'");

            Assert.That(WeaponXmlAttrs.AttrF(merged, "knock", -1f), Is.EqualTo(0f).Within(0.001f),
                        "an explicit 0 must win, not fall back to the base 3");
            Assert.That(WeaponXmlAttrs.AttrF(merged, "rapid", -1f), Is.EqualTo(10f).Within(0.001f),
                        "and the omitted attribute still inherits");
        }

        [Test]
        public void MergeRuns_AttributeOnlyInTheOverride_IsAppended()
        {
            // `flamer`'s tier 1 is the case in the data: it introduces phis attributes the base lacks.
            string merged = WeaponVariantXml.MergeRuns(" speed='150'", " flame='2'");

            Assert.That(WeaponXmlAttrs.AttrF(merged, "speed", 0f), Is.EqualTo(150f).Within(0.001f), "base kept");
            Assert.That(WeaponXmlAttrs.AttrF(merged, "flame", 0f), Is.EqualTo(2f).Within(0.001f),   "added");
        }

        [Test]
        public void MergeRuns_EmptyOverride_ReturnsTheBaseUnchanged()
        {
            const string tier0 = " maxhp='150' damage='6'";

            Assert.AreEqual(tier0, WeaponVariantXml.MergeRuns(tier0, ""),
                            "a weapon whose node does not repeat must not be perturbed");
            Assert.AreEqual(tier0, WeaponVariantXml.MergeRuns(tier0, null));
        }

        [Test]
        public void MergeRuns_ResultSatisfiesTheReaderBoundary()
        {
            // WeaponXmlAttrs' pattern is `(?:^|\s)name=…`, and its own remarks require every run to begin
            // with whitespace. A merge that dropped the leading space would make the FIRST attribute of
            // every merged run unreadable — silently, and only for that one attribute.
            string merged = WeaponVariantXml.MergeRuns("", " damage='7'");

            Assert.AreEqual(' ', merged[0], "merged runs must keep the leading whitespace the reader needs");
            Assert.That(WeaponXmlAttrs.AttrF(merged, "damage", -1f), Is.EqualTo(7f).Within(0.001f));
        }

        [Test]
        public void MergeRuns_DoesNotCollideOnSuffixedAttributeNames()
        {
            // The `expl` ⊂ `damexpl` family, re-checked through the merge rather than the single reader:
            // a merge implemented as string concatenation with a name search would resurrect the bug the
            // boundary fix removed.
            string merged = WeaponVariantXml.MergeRuns(" damexpl='750' expl='300'", " expl='400'");

            Assert.That(WeaponXmlAttrs.AttrF(merged, "expl",    0f), Is.EqualTo(400f).Within(0.001f), "expl");
            Assert.That(WeaponXmlAttrs.AttrF(merged, "damexpl", 0f), Is.EqualTo(750f).Within(0.001f),
                        "damexpl must survive an `expl` override");
        }

        // ── Runs ─────────────────────────────────────────────────────────────

        [Test]
        public void Runs_MatchesSelfClosingAndOpenForms_AndStopsAtTheTagBoundary()
        {
            var runs = WeaponVariantXml.Runs("<char a='1'/><char b='2'></char>", "char");

            Assert.AreEqual(2, runs.Count);
            Assert.AreEqual(" a='1'", runs[0]);
            Assert.AreEqual(" b='2'", runs[1]);
        }

        [Test]
        public void Runs_DoesNotLeakANestedChildIntoItsParentRun()
        {
            // The run must stop at the parent's own '>', or `<phis speed='150'/>` inside a <char> body
            // would be read as a char attribute.
            var runs = WeaponVariantXml.Runs("<char a='1'><phis speed='150'/></char>", "char");

            Assert.AreEqual(1, runs.Count);
            Assert.AreEqual(" a='1'", runs[0]);
            Assert.That(WeaponXmlAttrs.AttrF(runs[0], "speed", -1f), Is.EqualTo(-1f).Within(0.001f),
                        "the child's attribute must not be visible on the parent");
        }

        // ── Read ─────────────────────────────────────────────────────────────

        [Test]
        public void Read_NoSecondChar_IsNotAVariant()
        {
            // 142 of the 213 weapons. `Stand.as:212` decides existence on `char.length() > 1` alone.
            var data = WeaponVariantXml.Read("<char maxhp='150'/><phis speed='150'/>", 1);

            Assert.AreEqual(0, data.Variant);
            Assert.IsFalse(data.IsPresent);
            Assert.AreEqual("", data.CharAttrs);
        }

        [Test]
        public void Read_SecondChar_CapturesEveryRepeatedKindAtTheSameIndex()
        {
            // The measured shape: <char> repeats on 71 weapons, <ammo> on 27, <phis> on 11. All three
            // must be captured at index 1 in one pass — reading only <char> drops the other two.
            const string body =
                "<char maxhp='150' damage='6'/>" +
                "<char maxhp='250' damage='7'/>" +
                "<phis speed='150' m='2'/>" +
                "<phis speed='2000'/>" +
                "<ammo holder='2' reload='45'/>" +
                "<ammo holder='320' reload='30'/>";

            var data = WeaponVariantXml.Read(body, 1);

            Assert.AreEqual(1, data.Variant);
            Assert.IsTrue(data.IsPresent);
            Assert.That(WeaponXmlAttrs.AttrI(data.CharAttrs, "maxhp", 0), Is.EqualTo(250));
            Assert.That(WeaponXmlAttrs.AttrF(data.PhisAttrs, "speed", 0f), Is.EqualTo(2000f).Within(0.001f));
            Assert.That(WeaponXmlAttrs.AttrI(data.AmmoAttrs, "holder", 0), Is.EqualTo(320));
        }

        [Test]
        public void Read_AKindThatDoesNotRepeat_LeavesItsRunEmpty()
        {
            // `lasr` repeats <char> and <ammo> but not <vis>; the empty run is what tells the merge to
            // leave the base <vis> alone.
            var data = WeaponVariantXml.Read("<char a='1'/><char b='2'/><vis vbul='laser'/>", 1);

            Assert.IsTrue(data.IsPresent);
            Assert.AreEqual("", data.VisAttrs, "a single <vis> is not an override");
            Assert.AreEqual("", data.ComAttrs);
        }

        [Test]
        public void Read_IndexWithNoSuchNode_IsNotAVariant()
        {
            var data = WeaponVariantXml.Read("<char a='1'/><char b='2'/>", 2);

            Assert.AreEqual(0, data.Variant, "only two <char> nodes exist, so tier 2 does not");
            Assert.IsFalse(data.IsPresent);
        }

        [Test]
        public void AttrsFor_UnknownKind_IsEmptyRatherThanAThrow()
        {
            var data = WeaponVariantXml.Read("<char a='1'/><char b='2'/>", 1);

            Assert.AreEqual("", data.AttrsFor("nonsense"));
            Assert.AreEqual(data.CharAttrs, data.AttrsFor("char"));
        }

        // ── UnreadAttributeNames: the constraint-12 report ──────────────────

        [Test]
        public void UnreadAttributeNames_NamesWhatThePortCannotRead()
        {
            // A phis override mixing a read attribute with two the port has no field for. `speed` must
            // NOT be reported; `drot` and `m` must be. Note the SECOND <phis> is what makes it an
            // override at all — a lone <phis> is the base node and yields no override run.
            var data = WeaponVariantXml.Read(
                "<char damage='6'/><char damage='7'/>" +
                "<phis speed='150'/><phis speed='150' drot='13' m='2'/>", 1);

            var unread = WeaponVariantXml.UnreadAttributeNames(data);

            CollectionAssert.Contains(unread, "phis.drot");
            CollectionAssert.Contains(unread, "phis.m");
            CollectionAssert.DoesNotContain(unread, "phis.speed");
        }

        [Test]
        public void UnreadAttributeNames_IsEmptyWhenEveryAttributeIsUnderstood()
        {
            // `oldshot`'s real tier-1 pair: every attribute on it has a port field.
            var data = WeaponVariantXml.Read(
                "<char maxhp='150' damage='6' kol='5' knock='3'/>" +
                "<char maxhp='250' damage='7' prec='7'/>", 1);

            CollectionAssert.IsEmpty(WeaponVariantXml.UnreadAttributeNames(data));
        }

        [Test]
        public void UnreadAttributeNames_NoVariant_ReportsNothing()
        {
            var data = WeaponVariantXml.Read("<char maxhp='150'/>", 1);

            CollectionAssert.IsEmpty(WeaponVariantXml.UnreadAttributeNames(data));
        }

        [Test]
        public void UnreadAttributeNames_IsSorted_SoTheImportLogIsStable()
        {
            // The five names the shipped file actually produces on a <phis> override, in the order the
            // import log will print them. See WeaponVariantXml.UnreadAttributeNames for why each is here
            // — four are attributes the port reads nowhere, and `recoil` is a field that exists on
            // WeaponDefinition and that the importer never fills.
            var data = WeaponVariantXml.Read(
                "<char damage='6'/><char damage='7'/>" +
                "<phis speed='1'/><phis speed='1' drot='1' m='1' massa='1' drot2='1' recoil='1'/>", 1);

            var unread = WeaponVariantXml.UnreadAttributeNames(data);

            CollectionAssert.AreEqual(
                new[] { "phis.drot", "phis.drot2", "phis.m", "phis.massa", "phis.recoil" }, unread);
        }

        // ── The whole path, end to end, on a real weapon body ───────────────

        [Test]
        public void Oldshot_TierOne_FoldsOverTierZeroAndYieldsTheOracleValues()
        {
            // The complete read -> merge -> read path for AllData.as:2795-2806, which is the weapon the
            // research report used as its worked example. Asserts the merged tier-1 numbers the oracle
            // produces (250 hp, 7 dmg, prec 7) AND that the three inherited ones survive.
            const string body =
                "<char maxhp='150' damage='6' kol='5' rapid='10' prec='6' knock='3' destroy='10'/>" +
                "<char maxhp='250' damage='7' rapid='10' prec='7'/>";

            var data = WeaponVariantXml.Read(body, 1);
            Assert.IsTrue(data.IsPresent, "oldshot has a second <char>");

            string merged = WeaponVariantXml.MergeRuns(
                WeaponVariantXml.Runs(body, "char")[0], data.CharAttrs);

            Assert.That(WeaponXmlAttrs.AttrI(merged, "maxhp",   0),  Is.EqualTo(250));
            Assert.That(WeaponXmlAttrs.AttrF(merged, "damage",  0f), Is.EqualTo(7f).Within(0.001f));
            Assert.That(WeaponXmlAttrs.AttrF(merged, "prec",    0f), Is.EqualTo(7f).Within(0.001f));
            Assert.That(WeaponXmlAttrs.AttrI(merged, "kol",     0),  Is.EqualTo(5));
            Assert.That(WeaponXmlAttrs.AttrF(merged, "knock",   0f), Is.EqualTo(3f).Within(0.001f));
            Assert.That(WeaponXmlAttrs.AttrF(merged, "destroy", 0f), Is.EqualTo(10f).Within(0.001f));
        }
    }

    /// <summary>
    /// Pins <see cref="WeaponVariantOverrides"/> — the <i>typed</i> read of a weapon's tier-1 runs, which
    /// is what the runtime actually consumes — plus the one property of <c>WeaponVariantStats.ApplyTo</c>
    /// that can be checked without a <c>ScriptableObject</c>.
    ///
    /// <para><b>Why this is a second fixture and not more of <see cref="WeaponVariantXmlTests"/>.</b>
    /// That one pins the <i>string</i> merge (<c>MergeRuns</c>); this one pins the layer above it, where
    /// "absent" has to survive as a sentinel rather than as a number. The two fail differently: a broken
    /// merge yields a wrong value in a string that a reader could still see, while a broken parse yields
    /// <c>0f</c> in a field nothing distinguishes from an authored zero — so the assertions here are
    /// almost all <b>pair</b>s of "this one is present" and "that one is absent", never a lone value.</para>
    ///
    /// <para><b>Why the fixtures are the real rows.</b> Every body below is the child elements of a
    /// weapon from <c>AllData.as</c>, in document order, exactly as the file writes them. The interesting
    /// cases are not synthetic: <c>ice</c> is a delta of one attribute per node kind, <c>anti</c> is the
    /// only weapon in the file with a tier-1 <c>&lt;snd&gt;</c> and it authors <c>recoil='0'</c>,
    /// <c>sword</c> authors a <c>long</c> that is <i>different</i> from its base tier, and
    /// <c>cflamer</c> repeats four of the seven kinds at once.</para>
    ///
    /// <para><b>What cannot be tested here, and is guarded instead.</b>
    /// <c>WeaponVariantStats.ApplyTo</c> takes a <see cref="PFE.Data.Definitions.WeaponDefinition"/>, which
    /// is a <c>ScriptableObject</c>: constructing one is an ECall and throws outside the editor (measured
    /// on <c>WeaponDispatchTests</c>). So its <i>behaviour</i> is editor-only and its <i>shape</i> is
    /// pinned by <see cref="ApplyTo_Lint_AppliesEveryParsedFieldExceptTheFiveTheOracleReadsAtTierZero"/>,
    /// which is a source lint and therefore runs everywhere.</para>
    /// </summary>
    [TestFixture]
    public class WeaponVariantOverridesTests
    {
        // ── Fixtures: real weapon bodies, verbatim ───────────────────────────

        /// <summary>
        /// <c>ice</c> (<c>AllData.as:3554-3566</c>), the magic weapon the whole feature was investigated
        /// through. Tier 1 raises <c>damage</c> 5→10, <c>mana</c> 5→13 and <c>price</c> 400→1800 — one
        /// attribute per node kind, and it says <b>nothing</b> about the other thirteen.
        /// </summary>
        private const string IceBody =
            "<char damage='5' kol='5' rapid='15' prec='4' tipdam='11' knock='5' destroy='10'/>" +
            "<char damage='10'/>" +
            "<phis speed='70' deviation='6'/>" +
            "<vis vbul='ice'/>" +
            "<snd shoot='ice' noise='300'/>" +
            "<ammo magic='200' mana='5'/>" +
            "<ammo mana='13'/>" +
            "<dop effect='ice' ch='0.1'/>" +
            "<com price='400' chance='0' stage='1' uniq='0'/>" +
            "<com price='1800'/>";

        /// <summary>
        /// <c>oldshot</c> (<c>AllData.as:2795-2806</c>). The worked example: hp 150→250, damage 6→7,
        /// prec 6→7, and <b>silence</b> about <c>kol</c>, <c>knock</c> and <c>destroy</c>.
        /// </summary>
        private const string OldshotBody =
            "<char maxhp='150' damage='6' kol='5' rapid='10' prec='6' knock='3' destroy='10'/>" +
            "<char maxhp='250' damage='7' rapid='10' prec='7'/>" +
            "<phis speed='150' m='2' drot='13' deviation='5' recoil='12' massa='5'/>" +
            "<vis tipdec='1' shell='1'/>" +
            "<snd shoot='oldshot_s' reload='shotgun_r' noise='1200'/>" +
            "<ammo holder='2' reload='45'/>" +
            "<com price='360' worth='4' chance='1' stage='1' uniq='1'/>";

        /// <summary>
        /// <c>anti</c> (<c>AllData.as</c>, the <c>tip='2'</c> sniper row). Two properties nothing else in
        /// the file has: the <b>only</b> tier-1 <c>&lt;snd&gt;</c> node (a 1-attribute override), and
        /// <c>recoil='0'</c> — an authored zero against a base of <c>recoil='10'</c>, which is the exact
        /// case a "parse, then treat 0 as absent" reader gets wrong.
        /// </summary>
        private const string AntiBody =
            "<char maxhp='250' damage='85' rapid='25' prec='24' antiprec='8' crit='2' critdam='0.5' " +
            "knock='15' destroy='30'/>" +
            "<char maxhp='300' damage='95' prec='27' pier='10' knock='18'/>" +
            "<phis speed='500' drot='8' deviation='2' recoil='10' massa='10' m='3'/>" +
            "<phis drot='10' deviation='2' recoil='0'/>" +
            "<vis tipdec='3' shell='1'/>" +
            "<snd shoot='anti_s' reload='sniper_r' noise='1200'/>" +
            "<snd shoot='spitfire_s' noise='2200'/>" +
            "<ammo holder='5' reload='60'/>" +
            "<dop vision='2' probiv='0.6'/>" +
            "<com price='5400' worth='5' chance='0.4' stage='5' rep='0.12' uniq='0'/>";

        /// <summary>
        /// <c>cflamer</c> — repeats <c>&lt;char&gt;</c>, <c>&lt;phis&gt;</c>, <c>&lt;vis&gt;</c>,
        /// <c>&lt;dop&gt;</c> and <c>&lt;com&gt;</c>, and its tier-1 <c>&lt;phis&gt;</c> omits
        /// <c>deviation</c> while its <c>&lt;char&gt;</c> omits <c>knock</c> (authored <c>'0'</c> at tier
        /// 0 — so a merged read must land on "no knockback", not on "inherit nothing"). It also has
        /// <b>no</b> second <c>&lt;ammo&gt;</c>, so <c>holder='600'</c> must survive.
        /// </summary>
        private const string CflamerBody =
            "<char maxhp='3000' damage='6' rapid='1' pier='16' prec='6' crit='0' tipdam='3' " +
            "knock='0' destroy='10'/>" +
            "<char maxhp='3600' damage='7' prec='8' pier='20'/>" +
            "<phis speed='40' drot='5' drot2='3' deviation='4' grav='1' massa='20' m='3'/>" +
            "<phis speed='45' drot='8' massa='15'/>" +
            "<vis tipdec='11' vbul='flame' flare='flame' bulanim='1'/>" +
            "<vis tipdec='11' vbul='flame2' flare='flame' bulanim='1'/>" +
            "<snd prep='flamer_s' t1='1540' t2='2840' reload='flamer_r' noise='400'/>" +
            "<ammo holder='600' rashod='3' reload='60'/>" +
            "<dop effect='igni' damage='10' ch='0.2'/>" +
            "<dop effect='igni' damage='13' ch='0.2'/>" +
            "<com price='3000' worth='5' chance='0.4' stage='5' rep='0.25' uniq='0'/>";

        /// <summary>
        /// <c>sword</c> — the case that proves tier-1 <c>long</c> is <i>not</i> a duplicate of tier 0:
        /// <c>long='80'</c> against <c>long='90'</c>, and <c>minlong</c> authored on both. The oracle
        /// reads <c>phis[0]</c> (<c>WClub.as:121</c>), so applying tier 1 here would lengthen the II's
        /// reach by 10 px. Also the only fixture whose base tier carries <c>crit='2'</c> while tier 1 is
        /// silent — so "crit survives" is observable.
        /// </summary>
        private const string SwordBody =
            "<char maxhp='500' damage='35' rapid='12' crit='2' tipdam='1' knock='5' destroy='10'/>" +
            "<char maxhp='1000' damage='40' pier='25'/>" +
            "<phis massa='5' m='2' minlong='30' long='80'/>" +
            "<phis massa='5' m='2' minlong='30' long='90'/>" +
            "<vis tipdec='4'/>" +
            "<snd shoot='m_med' fall='fall_hammer' noise='300'/>" +
            "<dop effect='cut' damage='3' ch='0.5'/>" +
            "<dop effect='cut' damage='4' ch='0.5'/>" +
            "<com price='3500' chance='0.2' stage='5' worth='4' rep='0' uniq='0'/>";

        // ── The existence test ───────────────────────────────────────────────

        [Test]
        public void Parse_WeaponWithNoSecondChar_ReturnsNull()
        {
            // `Stand.as:212` decides existence on `char.length() > 1` alone, so 142 of the 213 weapons
            // have no tier at all. Null — not an all-absent instance — because the runtime's `Effective`
            // branches on it, and an empty instance would make `variant > 0` silently meaningful.
            var data = WeaponVariantXml.Read("<char maxhp='150'/><phis speed='150'/>", 1);

            Assert.IsFalse(data.IsPresent);
            Assert.IsNull(WeaponVariantOverrides.Parse(data));
        }

        [Test]
        public void Parse_IceTier1_IsADeltaNotACopyOfTheNode()
        {
            // The headline case. Three values present, and the four the node is silent about must read
            // ABSENT — not 0, which would zero a shotgun's pellet count and its knockback.
            var o = WeaponVariantOverrides.Parse(WeaponVariantXml.Read(IceBody, 1));

            Assert.IsNotNull(o);
            Assert.That(o.Damage, Is.EqualTo(10f).Within(0.001f), "char damage 5 -> 10");
            Assert.That(o.Mana,   Is.EqualTo(13f).Within(0.001f), "ammo mana 5 -> 13");
            Assert.That(o.Price,  Is.EqualTo(1800f).Within(0.001f), "com price 400 -> 1800, not 3x");

            Assert.IsFalse(WeaponVariantOverrides.Has(o.Knock),   "knock 5 must survive tier 1");
            Assert.IsFalse(WeaponVariantOverrides.Has(o.Rapid),   "rapid 15 must survive tier 1");
            Assert.IsFalse(WeaponVariantOverrides.Has(o.Kol),     "kol 5 must survive tier 1");
            Assert.IsFalse(WeaponVariantOverrides.Has(o.Magic),   "magic 200 must survive tier 1");
            Assert.IsFalse(WeaponVariantOverrides.Has(o.Prec),    "prec 4 must survive tier 1");
            Assert.IsFalse(WeaponVariantOverrides.Has(o.MaxHp),   "ice authors no maxhp at either tier");
        }

        [Test]
        public void Parse_OldshotTier1_LeavesTheThreeInheritedAttributesAbsent()
        {
            // The merge fixture's typed twin. `rapid='10'` is authored and EQUAL to the base — it must
            // still read present, because presence comes from the node, not from a value comparison.
            var o = WeaponVariantOverrides.Parse(WeaponVariantXml.Read(OldshotBody, 1));

            Assert.That(o.MaxHp,  Is.EqualTo(250f).Within(0.001f));
            Assert.That(o.Damage, Is.EqualTo(7f).Within(0.001f));
            Assert.That(o.Prec,   Is.EqualTo(7f).Within(0.001f));
            Assert.IsTrue(WeaponVariantOverrides.Has(o.Rapid), "authored, even though it matches tier 0");

            Assert.IsFalse(WeaponVariantOverrides.Has(o.Kol),     "kol 5 inherited");
            Assert.IsFalse(WeaponVariantOverrides.Has(o.Knock),   "knock 3 inherited");
            Assert.IsFalse(WeaponVariantOverrides.Has(o.Destroy), "destroy 10 inherited");
        }

        // ── The presence distinction: an authored 0 is not an absent attribute ─

        [Test]
        public void Parse_ExplicitZeroKnock_IsPresentAndZero()
        {
            // `knock='0'` means "no knockback", not "inherit". A parser that treated 0 as absent would
            // give this weapon the base 3 and pass every "the override wins" test.
            var o = WeaponVariantOverrides.Parse(WeaponVariantXml.Read(
                "<char knock='3' rapid='10'/><char knock='0'/>", 1));

            Assert.IsTrue(WeaponVariantOverrides.Has(o.Knock), "an authored 0 is present");
            Assert.That(o.Knock, Is.EqualTo(0f).Within(0.001f));
            Assert.IsFalse(WeaponVariantOverrides.Has(o.Rapid), "and the omitted one still inherits");
        }

        [Test]
        public void Parse_AntiRecoilZero_IsPresentAndZero()
        {
            // The same distinction on a real row: `anti` goes recoil 10 -> 0. `recoilFrames` is applied
            // presence-gated in ApplyTo, so a parser that dropped the zero would leave the II recoiling
            // like the base weapon.
            var o = WeaponVariantOverrides.Parse(WeaponVariantXml.Read(AntiBody, 1));

            Assert.IsTrue(WeaponVariantOverrides.Has(o.Recoil));
            Assert.That(o.Recoil, Is.EqualTo(0), "recoil='0' is an override to zero");
            Assert.That(o.Deviation, Is.EqualTo(2f).Within(0.001f), "and deviation is re-authored as 2");
        }

        // ── The two recomputes, and the tri-state that makes `auto` one ──────

        [Test]
        public void Parse_Auto_IsATriStateNotABool()
        {
            // `getCharParam` assigns `auto = rapid <= 6` on EVERY pass, then `@auto` if present. So
            // absent is not "false" and not "inherit" — it is "re-derive from the merged rapid", which
            // the port encodes as autoMode 0. Seven tier-1 nodes author it; fourteen weapons depend on
            // the re-derivation.
            Assert.That(ParseChar("<char rapid='10'/><char rapid='10'/>").Auto, Is.EqualTo(-1),
                        "absent");
            Assert.That(ParseChar("<char rapid='10'/><char auto='0' rapid='10'/>").Auto, Is.EqualTo(0),
                        "authored false");
            Assert.That(ParseChar("<char rapid='10'/><char auto='1' rapid='10'/>").Auto, Is.EqualTo(1),
                        "authored true");
        }

        [Test]
        public void Parse_Massa_IsSentinelWhenSilentAndRawWhenAuthored()
        {
            // `massa = @massa > 0 ? @massa/50 : 0` is assigned unconditionally, so silence means "reset
            // to 0", not "keep the base". The parser must therefore report absent — ApplyTo turns that
            // into the oracle's 0 — and must not pre-divide by 50 (the port stores it raw and clamps
            // with 50/massa at the point of use).
            Assert.IsFalse(WeaponVariantOverrides.Has(ParseChar("<char a='1'/><char a='1'/>").Massa));

            var cflamer = WeaponVariantOverrides.Parse(WeaponVariantXml.Read(CflamerBody, 1));
            Assert.That(cflamer.Massa, Is.EqualTo(15f).Within(0.001f), "20 -> 15, stored raw");

            var sword = WeaponVariantOverrides.Parse(WeaponVariantXml.Read(SwordBody, 1));
            Assert.That(sword.Massa, Is.EqualTo(5f).Within(0.001f), "re-authored at the same value");
        }

        // ── Multi-kind rows ─────────────────────────────────────────────────

        [Test]
        public void Parse_AntiTier1_ReadsTheOnlySndOverrideInTheFile()
        {
            // One weapon in the whole file repeats `<snd>`. If the reader only handled `<char>` this
            // would be a weapon whose tier-1 sound silently stayed at the base — audible, not a crash.
            var o = WeaponVariantOverrides.Parse(WeaponVariantXml.Read(AntiBody, 1));

            Assert.That(o.Shoot, Is.EqualTo("spitfire_s"), "anti_s -> spitfire_s");
            Assert.That(o.Noise, Is.EqualTo(2200f).Within(0.001f), "1200 -> 2200");
            Assert.IsNull(o.Reload, "the tier-1 node does not repeat `reload`, so sniper_r survives");
            Assert.IsFalse(WeaponVariantOverrides.Has(o.Reload));
        }

        [Test]
        public void Parse_CflamerTier1_ReadsFourKindsAndLeavesTheFifthAlone()
        {
            var o = WeaponVariantOverrides.Parse(WeaponVariantXml.Read(CflamerBody, 1));

            // <phis>
            Assert.That(o.Speed, Is.EqualTo(45f).Within(0.001f), "40 -> 45");
            Assert.IsFalse(WeaponVariantOverrides.Has(o.Deviation), "deviation 4 inherited");
            Assert.IsFalse(WeaponVariantOverrides.Has(o.Grav), "grav 1 inherited");

            // <vis>
            Assert.That(o.Vbul, Is.EqualTo("flame2"), "flame -> flame2");
            Assert.That(o.Flare, Is.EqualTo("flame"));
            Assert.That(o.Tipdec, Is.EqualTo(11));
            Assert.That(o.BulAnim, Is.EqualTo(1), "presence-only flag");

            // <dop>
            Assert.That(o.Effect, Is.EqualTo("igni"));
            Assert.That(o.DopDamage, Is.EqualTo(13f).Within(0.001f), "10 -> 13");
            Assert.That(o.DopCh, Is.EqualTo(0.2f).Within(0.001f));

            // <ammo> does NOT repeat: 600 rounds must survive, and that is the control that keeps the
            // four positives above from being satisfied by a reader that returned the whole node.
            Assert.IsFalse(WeaponVariantOverrides.Has(o.Holder));
            Assert.IsFalse(WeaponVariantOverrides.Has(o.Rashod));
        }

        [Test]
        public void Parse_SwordTier1_ParsesLongAndMinLong_WhichApplyToMustNotUse()
        {
            // Parsed so the data surface is visible, never applied: `WClub.as:121-129` reads
            // `node.phis[0]`. The "not applied" half is the lint below — this half only pins that the
            // values are reachable, so a future reader cannot claim the field was dropped by accident.
            var o = WeaponVariantOverrides.Parse(WeaponVariantXml.Read(SwordBody, 1));

            Assert.That(o.Long,    Is.EqualTo(90f).Within(0.001f), "tier 1 says 90");
            Assert.That(o.MinLong, Is.EqualTo(30f).Within(0.001f));
            Assert.That(o.MaxHp,   Is.EqualTo(1000f).Within(0.001f), "500 -> 1000");
            Assert.That(o.Pier,    Is.EqualTo(25f).Within(0.001f), "pier appears only at tier 1");
            Assert.IsFalse(WeaponVariantOverrides.Has(o.Crit), "crit 2 inherited from tier 0");
            Assert.IsFalse(WeaponVariantOverrides.Has(o.Power), "pow is index-0 only");
            Assert.IsFalse(WeaponVariantOverrides.Has(o.Combo), "combo is index-0 only");
        }

        [Test]
        public void Parse_VariantWithEveryRunEmpty_ReportsEverythingAbsent()
        {
            // A tier whose nodes are present but attribute-less. Not in the shipped data, but it is what
            // a partially-imported asset looks like (Unity zero-fills), and the honest answer is "the
            // tier exists and overrides nothing" rather than an exception or a silent all-zero.
            var data = WeaponVariantXml.Read("<char maxhp='1'/><char/>", 1);
            var o = WeaponVariantOverrides.Parse(data);

            Assert.IsNotNull(o, "the tier exists");
            Assert.IsTrue(data.IsPresent);

            foreach (System.Reflection.FieldInfo field in ParsedFields())
            {
                object value = field.GetValue(o);
                bool present = value is float f
                    ? WeaponVariantOverrides.Has(f)
                    : value is int i
                        ? WeaponVariantOverrides.Has(i)
                        : WeaponVariantOverrides.Has((string)value);

                Assert.IsFalse(present, field.Name + " must be absent, not zero");
            }
        }

        /// <summary>
        /// An attribute-less node <b>before</b> a real sibling must keep the real one at index 1.
        ///
        /// <para><b>This is the one place in the shipped file where the old reader was wrong.</b>
        /// <c>spear</c> (<c>AllData.as:2223-2228</c>) is <c>&lt;dop/&gt;</c> followed by
        /// <c>&lt;dop effect='poison' damage='2' ch='0.5'/&gt;</c>. The reader's pattern required
        /// whitespace after the tag name, so <c>&lt;dop/&gt;</c> was invisible: it reported <b>one</b>
        /// dop node and that node was the tier-1 one. The consequence was double — the importer read it
        /// as index 0 (the base tier gained poison it should not have) and the variant read found
        /// nothing at index 1 (the override vanished). Both are asserted here.</para>
        /// </summary>
        [Test]
        public void Read_EmptyNodeBeforeARealOne_KeepsTheIndex()
        {
            const string body =
                "<char maxhp='100' damage='32' rapid='20' tipdam='1' knock='5' destroy='10' pow='1'/>" +
                "<char maxhp='200' damage='30'/>" +
                "<phis massa='2' m='2' long='50'/>" +
                "<vis tipdec='4'/>" +
                "<dop/>" +
                "<dop effect='poison' damage='2' ch='0.5'/>" +
                "<snd shoot='m_med' fall='falll_item' noise='400'/>";

            var runs = WeaponVariantXml.Runs(body, "dop");
            Assert.AreEqual(2, runs.Count, "an attribute-less node is still a node");

            var data = WeaponVariantXml.Read(body, 1);
            var o = WeaponVariantOverrides.Parse(data);

            Assert.That(o.Effect, Is.EqualTo("poison"), "the tier-1 poison must be reachable at index 1");
            Assert.That(o.DopDamage, Is.EqualTo(2f).Within(0.001f));
            Assert.That(o.DopCh, Is.EqualTo(0.5f).Within(0.001f));

            // The control: index 0 is EMPTY, so the base tier must carry no dop at all. A reader that
            // dropped the empty node would make this the poison run — which is what the importer did.
            Assert.AreEqual("", WeaponVariantXml.Runs(body, "dop")[0].Trim(),
                            "index 0 must be the empty node, not the tier-1 one");
        }

        // ── The lint: ApplyTo's shape, which cannot be exercised offline ─────

        /// <summary>
        /// Every field <see cref="WeaponVariantOverrides"/> parses is either written by
        /// <c>WeaponVariantStats.ApplyTo</c> or is one of the five that must <b>not</b> be.
        ///
        /// <para><b>Why a lint and not a test.</b> <c>ApplyTo</c>'s parameter is a
        /// <see cref="PFE.Data.Definitions.WeaponDefinition"/>, a <c>ScriptableObject</c>; creating one is
        /// an ECall, so the method cannot run offline at all. Its <i>behaviour</i> therefore has no
        /// executable guard — but its <i>shape</i> does, and shape is what fails silently here: a field
        /// added to the parser and never written reads as the base value with no error anywhere.</para>
        ///
        /// <para><b>The expected set is a diff, not a list.</b> The five unapplied names are asserted as
        /// the <i>complete</i> difference between "fields the type declares" and "names mentioned in
        /// <c>ApplyTo</c>". Adding a field without wiring it, or wiring one of these five, both fail —
        /// and neither can be satisfied by editing a hand-maintained list in this fixture.</para>
        /// </summary>
        [Test]
        public void ApplyTo_Lint_AppliesEveryParsedFieldExceptTheFiveTheOracleReadsAtTierZero()
        {
            string body = ApplyToBody();

            CollectionAssert.AreEqual(
                new[] { "Combo", "Long", "MinLong", "Power", "Vweap" },
                UnappliedIn(body),
                "the set of parsed-but-unwritten fields changed: either a new field was added to " +
                "WeaponVariantOverrides without a write in ApplyTo, or one of the five documented " +
                "exceptions was wired up (which would diverge from the oracle — see the comments at " +
                "the write sites)");

            // The positive control for the scan itself: if reflection returned nothing, the diff above
            // would be an empty set against an empty expectation and this lint would pass vacuously.
            Assert.That(ParsedFields().Length, Is.GreaterThan(50), "the override surface must be large");

            // The two writes that were REMOVED, pinned by absence: `WClub` reads phis[0], so a tier-1
            // reach must never reach the definition. `sword` authors 80 vs 90, so this is not academic.
            Assert.That(body, Does.Not.Contain("meleeDlina"));
            Assert.That(body, Does.Not.Contain("meleeMinDlina"));
            Assert.That(body, Does.Not.Contain("meleePowerAttack"));
            Assert.That(body, Does.Not.Contain("meleeCombo"));
        }

        [Test]
        public void TheUnappliedScan_DetectsARemovedWrite()
        {
            // A guard that cannot fail is not a guard. Deleting one write from a copy of the real method
            // body must make the scan report that field — otherwise the assertion above would pass on a
            // method that had lost every line it checks for.
            string mutated = ApplyToBody().Replace("o.Damage", "");

            Assert.That(mutated, Is.Not.EqualTo(ApplyToBody()), "the mutation must actually change the body");
            Assert.That(UnappliedIn(mutated), Does.Contain("Damage"),
                        "the scan must notice a write that is no longer there");
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private static WeaponVariantOverrides ParseChar(string body)
            => WeaponVariantOverrides.Parse(WeaponVariantXml.Read(body, 1));

        private static System.Reflection.FieldInfo[] ParsedFields()
            => typeof(WeaponVariantOverrides)
                .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        /// <summary>The stripped source of <c>ApplyTo</c>, with comments and literals removed.</summary>
        private static string ApplyToBody()
            => SourceLint.MethodBody(
                SourceLint.ReadStripped("Data/Definitions/WeaponDefinition.cs"),
                "public static void ApplyTo(");

        /// <summary>
        /// The parsed fields <paramref name="body"/> never reads, ordinal-sorted. A field is "read" when
        /// the body mentions <c>o.Name</c> — the parameter is named <c>o</c> in the signature this
        /// fixture asserts on, so a rename must update this fixture rather than quietly stop scanning.
        /// </summary>
        private static string[] UnappliedIn(string body)
            => ParsedFields()
                .Select(f => f.Name)
                .Where(n => body.IndexOf("o." + n, System.StringComparison.Ordinal) < 0)
                .OrderBy(n => n, System.StringComparer.Ordinal)
                .ToArray();
    }
}
