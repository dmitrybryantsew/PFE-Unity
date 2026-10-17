using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using PFE.Systems.Items;
using PFE.Systems.Weapons;

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
}
