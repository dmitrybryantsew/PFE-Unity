using System.IO;
using NUnit.Framework;
using PFE.Tests.Editor.Common;
using PFE.UI.PipBuck;

namespace PFE.Tests.EditMode.UI
{
    /// <summary>
    /// Pins <see cref="PipTextTable"/> — the port of <c>fe.Res</c>'s lookup half (<c>Res.as:55-147</c>)
    /// — and the three captions in the PipBuck that now resolve through it.
    ///
    /// <para><b>Two fixtures in one, and the split matters.</b> The first half runs against a small
    /// inline document and pins the <i>rules</i>: the alias table, the <c>razd</c> child index, the
    /// default-language fallback, the missing-key marker, and the <c>m="1"</c> mature variant. The
    /// second half runs against the <b>shipped <c>text_en.xml</c></b> and pins the <i>data</i>: the
    /// captions the player will actually read, and — the part that keeps the gap ledger honest —
    /// that every key the port asks for is present. A rule fixture that never sees the real document
    /// cannot notice that a key was renamed; a data fixture cannot notice that a branch is wrong.</para>
    ///
    /// <para><b>Why the file is read from disk and not from a <c>TextAsset</c>.</b>
    /// <c>Resources.Load</c> is a Unity ECall and throws when this assembly is executed outside the
    /// editor, which is how this project's offline harness runs its fixtures. <c>SourceLint.ProjectRoot</c>
    /// already solves that for the lints; this reuses it rather than inventing a second way to find
    /// the repository.</para>
    /// </summary>
    [TestFixture]
    public class PipTextTableTests
    {
        // ── The inline document ───────────────────────────────────────────────
        //
        // Deliberately NOT the shipped file: these assertions are about Res.txt's branches, and a
        // branch test that runs against 2000 entries cannot say which entry it depended on.

        private const string InlineXml = @"<all>
  <pip id=""plain""><n>Alpha</n><info>Alpha description</info></pip>
  <pip id=""mature"" m=""1""><n>Clean|Mature</n></pip>
  <pip id=""pipeOnly""><n>a|b|c</n></pip>
  <eff id=""fx""><n>An effect</n></eff>
  <obj id=""crate""><n>Crate</n></obj>
</all>";

        private static PipTextTable Inline() => PipTextTable.Parse(InlineXml);

        // ── The alias table and the child index (Res.as:17-31) ────────────────

        [Test]
        public void TableFor_IsTheOracleAliasTable()
        {
            Assert.That(PipTextTable.TableFor("p"), Is.EqualTo("pip"), "Res.pipText is Res.txt(\"p\", …).");
            Assert.That(PipTextTable.TableFor("e"), Is.EqualTo("eff"));
            Assert.That(PipTextTable.TableFor("g"), Is.EqualTo("gui"));
            Assert.That(PipTextTable.TableFor("o"), Is.EqualTo("obj"), "the alias is 'o', the table is 'obj'.");

            // An unknown alias must not fall through to "the table named like the alias" — 'pip'
            // happens to be a real table, so a passthrough here would look like it worked.
            Assert.That(PipTextTable.TableFor("x"), Is.Null);
            Assert.That(PipTextTable.TableFor("pip"), Is.Null, "the aliases are single letters; 'pip' is a table name.");
        }

        [Test]
        public void ChildFor_IsTheRazdTable()
        {
            Assert.That(PipTextTable.ChildFor(0), Is.EqualTo("n"));
            Assert.That(PipTextTable.ChildFor(1), Is.EqualTo("info"));
            Assert.That(PipTextTable.ChildFor(2), Is.EqualTo("mess"));
            Assert.That(PipTextTable.ChildFor(3), Is.EqualTo("help"));
            Assert.That(PipTextTable.ChildFor(4), Is.Null);
            Assert.That(PipTextTable.ChildFor(-1), Is.Null);
        }

        // ── Res.txt's branches ────────────────────────────────────────────────

        [Test]
        public void Text_ResolvesTheDefaultChild()
        {
            Assert.That(Inline().Text("p", "plain"), Is.EqualTo("Alpha"));
            Assert.That(Inline().Pip("plain"), Is.EqualTo("Alpha"), "Pip() is Text(\"p\", …).");
            Assert.That(Inline().Eff("fx"), Is.EqualTo("An effect"));
        }

        [Test]
        public void Text_ResolvesANonDefaultChild()
        {
            // Res.as:71 — `s = xl[clob[razd]][0]`, so razd 1 is the <info> child.
            Assert.That(Inline().Text("p", "plain", 1), Is.EqualTo("Alpha description"));
            Assert.That(Inline().Text("p", "plain", 2), Is.EqualTo(string.Empty),
                "the entry has no <mess> child, and a missing child at razd != 0 is the empty string.");
        }

        [Test]
        public void Text_MissingKeyAtRazdZero_ReturnsTheOraclesMarker()
        {
            // Res.as:93-95 — `return "*" + clob[tip] + "_" + id;`
            Assert.That(Inline().Text("p", "nosuchkey"), Is.EqualTo("*pip_nosuchkey"));
        }

        [Test]
        public void Text_MissingKeyAtAnotherRazd_IsEmpty()
        {
            // Res.as:97 — `return "";`
            Assert.That(Inline().Text("p", "nosuchkey", 1), Is.EqualTo(string.Empty));
        }

        [Test]
        public void Text_TheObjTableNeverReportsAMiss()
        {
            // Res.as:89-91 — an unnamed object is legal, so `o` returns "" rather than a marker.
            Assert.That(Inline().Text("o", "nosuchkey"), Is.EqualTo(string.Empty));
            Assert.That(Inline().Text("o", "crate"), Is.EqualTo("Crate"), "a hit still resolves.");
        }

        [Test]
        public void Text_EmptyIdIsEmpty()
        {
            // Res.as:64-67 — checked before the lookup, so it is empty for every table including obj.
            Assert.That(Inline().Text("p", ""), Is.EqualTo(string.Empty));
            Assert.That(Inline().Text("p", null), Is.EqualTo(string.Empty));
            Assert.That(Inline().Text("o", ""), Is.EqualTo(string.Empty));
        }

        [Test]
        public void Text_UnknownAliasOrRazd_IsEmptyRatherThanAMarker()
        {
            // The Oracle would throw inside the try and swallow it (Res.as:73-75), leaving s null and
            // taking the marker path — but clob[tip] is undefined, so the marker would read
            // "*undefined_x". The port refuses to invent that; the contract is "no alias, no text".
            Assert.That(Inline().Text("x", "plain"), Is.EqualTo(string.Empty));
            Assert.That(Inline().Text("p", "plain", 9), Is.EqualTo(string.Empty));
        }

        [Test]
        public void Text_FallsBackToTheDefaultLanguageDocument()
        {
            // Res.as:76-86 — Res.d first, Res.e second. World.as:531-539 makes Res.e the DEFAULT
            // language and Res.d the selected one.
            const string active = @"<all><pip id=""onlyInDefault""></pip><pip id=""here""><n>Active</n></pip></all>";
            const string fallback = @"<all><pip id=""onlyInDefault""><n>Default</n></pip></all>";

            PipTextTable table = PipTextTable.Parse(active, fallback);

            Assert.That(table.Text("p", "here"), Is.EqualTo("Active"), "the selected document wins.");
            Assert.That(table.Text("p", "onlyInDefault"), Is.EqualTo("Default"),
                "a key missing from the selected document comes from the default one.");
        }

        [Test]
        public void Text_AppliesTheMatureVariantSplit()
        {
            // Res.as:100-107 — `if(xl.@m == "1") s = s.split("|")[matFilter ? 1 : 0]`.
            PipTextTable table = Inline();

            bool original = PipTextTable.MatureContentFilter;
            try
            {
                PipTextTable.MatureContentFilter = false;
                Assert.That(table.Text("p", "mature"), Is.EqualTo("Clean"),
                    "the filtered build takes the FIRST part.");

                PipTextTable.MatureContentFilter = true;
                Assert.That(table.Text("p", "mature"), Is.EqualTo("Mature"),
                    "and the unfiltered build the second.");
            }
            finally
            {
                PipTextTable.MatureContentFilter = original;
            }
        }

        [Test]
        public void Text_LeavesAPipeAloneUnlessTheEntryIsMarkedMature()
        {
            // Res.as:100 — the split is gated on `xl.@m == "1"`, not on the text containing a `|`.
            // Without the gate, any caption with a pipe in it would lose everything after the first.
            Assert.That(Inline().Text("p", "pipeOnly"), Is.EqualTo("a|b|c"),
                "the entry carries no m=\"1\", so no split happens at all.");

            // And when it IS marked, `spl[0]` / `spl[1]` are taken and the rest dropped —
            // `split()` returns every part but the Oracle only ever indexes two.
            PipTextTable marked = PipTextTable.Parse(
                @"<all><pip id=""three"" m=""1""><n>a|b|c</n></pip></all>");

            Assert.That(marked.Text("p", "three"), Is.EqualTo("a"),
                "matFilter off takes part 0 and discards parts 2..n.");
        }

        // ── Degenerate documents ──────────────────────────────────────────────

        [Test]
        public void Parse_OfNothingIsTheEmptyTable()
        {
            Assert.That(PipTextTable.Parse(null).IsEmpty, Is.True);
            Assert.That(PipTextTable.Parse("").IsEmpty, Is.True);
            Assert.That(PipTextTable.Parse("   ").IsEmpty, Is.True);
            Assert.That(PipTextTable.Empty.IsEmpty, Is.True);

            Assert.That(PipTextTable.Empty.Text("p", "anything"), Is.EqualTo("*pip_anything"),
                "an empty table still reports misses the Oracle's way.");
            Assert.That(PipTextTable.Empty.TryGet("p", "anything", 0, out _), Is.False);
        }

        [Test]
        public void TryGet_ReportsPresenceWithoutTheMarker()
        {
            PipTextTable table = Inline();

            Assert.That(table.TryGet("p", "plain", 0, out string text), Is.True);
            Assert.That(text, Is.EqualTo("Alpha"));

            Assert.That(table.TryGet("p", "nosuchkey", 0, out _), Is.False,
                "TryGet is how a caller distinguishes 'no document' from 'no such key'.");

            Assert.That(table.CountOf("pip"), Is.EqualTo(3));
            Assert.That(table.CountOf("nosuchtable"), Is.EqualTo(0));
            Assert.That(table.Count, Is.EqualTo(5), "3 pip + 1 eff + 1 obj.");
        }

        // ── The shipped document ──────────────────────────────────────────────

        private static PipTextTable _shipped;

        /// <summary>
        /// The shipped document, loaded on first use.
        ///
        /// <para><b>Lazy, not <c>[OneTimeSetUp]</c>.</b> The offline wall
        /// (<c>.workbuddy-ai/tools/agentverify/wall/Runner.cs</c>) dispatches
        /// <c>SetUpAttribute</c> and <c>TearDownAttribute</c> and nothing else — it has no
        /// <c>OneTimeSetUpAttribute</c> branch at all. A fixture that arranges there gets a null
        /// field and every test in it fails with a <c>NullReferenceException</c> that names the wrong
        /// thing, while the same fixture is green in the editor. A lazily-initialised property has no
        /// such dependency and behaves identically in both hosts.</para>
        /// </summary>
        private static PipTextTable Shipped
        {
            get
            {
                if (_shipped != null) return _shipped;

                string path = Path.Combine(
                    SourceLint.ProjectRoot(), "Assets", "_PFE", "Data", "Resources", "Localisation", "text_en.xml");

                Assert.That(File.Exists(path), Is.True,
                    "The localisation document is missing: " + path + ". It is a verbatim copy of " +
                    "text_en.xml from the shipping build — the file lang.xml names for `en`, and the " +
                    "document the Oracle tree never contained. Without it every PipBuck caption falls " +
                    "back to the port's pre-localisation English.");

                _shipped = PipTextTable.Parse(File.ReadAllText(path));

                Assert.That(_shipped.IsEmpty, Is.False, "The document parsed to zero entries: " + path);

                return _shipped;
            }
        }

        [Test]
        public void Shipped_HoldsTheWholeDocument()
        {
            // A truncated copy would still parse. This is the guard against one.
            Assert.That(Shipped.Count, Is.GreaterThan(2000),
                "text_en.xml carries ~2000 id-bearing entries across its tables; a much smaller " +
                "count means the copy is partial.");

            Assert.That(Shipped.CountOf("pip"), Is.EqualTo(424));
            Assert.That(Shipped.CountOf("eff"), Is.EqualTo(194));
            Assert.That(Shipped.CountOf("unit"), Is.EqualTo(190));
        }

        [Test]
        public void Shipped_GivesThePipBuckTabCaptions()
        {
            // These are Res.pipText("main" + n) — PipBuck.as:172, and :110 / :177 for but0.
            Assert.That(Shipped.Pip("main0"), Is.EqualTo("Close [TAB]"));
            Assert.That(Shipped.Pip("main1"), Is.EqualTo("Status"));
            Assert.That(Shipped.Pip("main2"), Is.EqualTo("Inventory"));
            Assert.That(Shipped.Pip("main3"), Is.EqualTo("Information"));
            Assert.That(Shipped.Pip("main5"), Is.EqualTo("Main menu"));
        }

        [Test]
        public void Shipped_ClosesTheMain4Gap()
        {
            // `main4` was recorded as "unobserved, so not invented": PipBuck.as:275 hides tab 4
            // unless a contextual page owns it and :279 relabels it, so its default caption never
            // reached the screen and no reference capture of it existed.
            Assert.That(Shipped.Pip("main4"), Is.EqualTo("Trading"));
        }

        [Test]
        public void Shipped_CorrectsTheMaincloseGuess()
        {
            // The reference capture read "Close [TAB]" on but0 and it was assumed to be `mainclose`.
            // It is not: `mainclose` is "Close". The capture was right about the SCREEN and wrong
            // about the KEY — toNormalMode (PipBuck.as:177) overwrites the button with `main0`, which
            // is "Close [TAB]". Both facts are pinned so neither can drift back.
            Assert.That(Shipped.Pip("mainclose"), Is.EqualTo("Close"));
            Assert.That(Shipped.Pip("main0"), Is.EqualTo("Close [TAB]"));
        }

        [Test]
        public void Shipped_GivesTheContextualPageCaptions()
        {
            // Pages 6..9 had no caption at all before: no capture of them existed, so the port showed
            // the upper-cased identifier. PipBuck.as:279 relabels tab 4 to `main` + the page id.
            Assert.That(Shipped.Pip("main6"), Is.EqualTo("Healing"));
            Assert.That(Shipped.Pip("main7"), Is.EqualTo("Craft"));
            Assert.That(Shipped.Pip("main8"), Is.EqualTo("Appearance"));
            Assert.That(Shipped.Pip("main9"), Is.EqualTo("Storage"));
        }

        [Test]
        public void Shipped_GivesTheInventorySubTabs()
        {
            // Res.pipText("inv" + page2) — PipPage.as:148.
            Assert.That(Shipped.Pip("inv1"), Is.EqualTo("Weapons"));
            Assert.That(Shipped.Pip("inv2"), Is.EqualTo("Armor"));
            Assert.That(Shipped.Pip("inv3"), Is.EqualTo("Equipment"));
            Assert.That(Shipped.Pip("inv4"), Is.EqualTo("Other"));
            Assert.That(Shipped.Pip("inv5"), Is.EqualTo("Ammunition"));
        }

        [Test]
        public void Shipped_GivesTheInventoryHeaderKeys()
        {
            // The gap ledger guessed `ii1..ii8`; there are nine, and the port only asks for six.
            Assert.That(Shipped.Pip("ii1"), Is.EqualTo("key"));
            Assert.That(Shipped.Pip("ii2"), Is.EqualTo("name"));
            Assert.That(Shipped.Pip("ii3"), Is.EqualTo("condition"));
            Assert.That(Shipped.Pip("ii4"), Is.EqualTo("ammunition"));
            Assert.That(Shipped.Pip("ii5"), Is.EqualTo("quantity"));
            Assert.That(Shipped.Pip("ii6"), Is.EqualTo("category"));
            Assert.That(Shipped.Pip("ii8"), Is.EqualTo("weight"));
        }

        [Test]
        public void Shipped_GivesTheEmptyListNotice()
        {
            // PipPageInv.as:310 — `vis.emptytext.text = Res.pipText("emptyinv")`.
            Assert.That(Shipped.Pip("emptyinv"), Is.EqualTo("No items in this category"));
        }

        // ── Every key the port asks for is present ────────────────────────────

        [Test]
        public void Shipped_ResolvesEveryTabKeyThePortUses()
        {
            // The self-checking half of the gap ledger: if a caption key is renamed in a future
            // document, this fails here rather than silently degrading to the fallback in a build.
            for (int pageId = 0; pageId <= PipBuckPages.MaxPageId; pageId++)
            {
                string key = PipBuckPages.LabelKeyFor(pageId);

                Assert.That(Shipped.TryGet("p", key, 0, out string caption), Is.True,
                    $"The tab key '{key}' (page {pageId}) is not in the document.");
                Assert.That(caption, Is.Not.Empty, $"The tab key '{key}' resolves to an empty caption.");
            }
        }

        [Test]
        public void Shipped_ResolvesEveryInventoryHeaderKeyThePortUses()
        {
            for (int subPage = 1; subPage <= PipInventoryList.SubPageCount; subPage++)
            {
                string[] keys = PipInventoryList.HeaderKeysFor(subPage);

                for (int column = 0; column < keys.Length; column++)
                {
                    // An empty key is the Oracle deliberately blanking a column (`statHead.ammo.text
                    // = ""`), not a missing entry.
                    if (keys[column].Length == 0) continue;

                    Assert.That(Shipped.TryGet("p", keys[column], 0, out _), Is.True,
                        $"Header key '{keys[column]}' (sub-page {subPage}, column {column}) is not in " +
                        "the document.");
                }
            }
        }

        [Test]
        public void Shipped_ResolvesEveryInventorySubTabKeyThePortUses()
        {
            for (int subPage = 1; subPage <= PipInventoryList.SubPageCount; subPage++)
            {
                Assert.That(Shipped.TryGet("p", "inv" + subPage, 0, out _), Is.True,
                    $"Sub-tab key 'inv{subPage}' is not in the document.");
            }
        }

        // ── The captions the port now shows ───────────────────────────────────

        [Test]
        public void TabLabelFor_UsesTheDocument()
        {
            Assert.That(PipBuckPages.TabLabelFor(0, Shipped), Is.EqualTo("Close [TAB]"));
            Assert.That(PipBuckPages.TabLabelFor(1, Shipped), Is.EqualTo("Status"));
            Assert.That(PipBuckPages.TabLabelFor(2, Shipped), Is.EqualTo("Inventory"));
            Assert.That(PipBuckPages.TabLabelFor(3, Shipped), Is.EqualTo("Information"));
            Assert.That(PipBuckPages.TabLabelFor(4, Shipped), Is.EqualTo("Trading"));
            Assert.That(PipBuckPages.TabLabelFor(5, Shipped), Is.EqualTo("Main menu"));
        }

        [Test]
        public void TabLabelFor_WithoutADocument_StillProducesACaption()
        {
            // The fallback path. 0..5 are the screenshot-derived words; 6..9 have no entry there and
            // degrade to the Oracle's own identifier, upper-cased — Oracle data, not invented prose.
            Assert.That(PipBuckPages.TabLabelFor(2, null), Is.EqualTo("Inventory"));
            Assert.That(PipBuckPages.TabLabelFor(4, null), Is.EqualTo("Trading"),
                "main4 was added to the fallback once the document attested it.");
            Assert.That(PipBuckPages.TabLabelFor(6, null), Is.EqualTo("MED"));
            Assert.That(PipBuckPages.TabLabelFor(9, null), Is.EqualTo("VAULT"));
        }

        [Test]
        public void TabLabelFor_TheDocumentAddsCaptionsTheFallbackCannot()
        {
            // The single clearest statement of what the import bought: pages 6..9 went from a
            // shouted identifier to the Oracle's own word.
            Assert.That(PipBuckPages.TabLabelFor(6, null), Is.EqualTo("MED"));
            Assert.That(PipBuckPages.TabLabelFor(6, Shipped), Is.EqualTo("Healing"));
            Assert.That(PipBuckPages.TabLabelFor(9, null), Is.EqualTo("VAULT"));
            Assert.That(PipBuckPages.TabLabelFor(9, Shipped), Is.EqualTo("Storage"));
        }

        [Test]
        public void TabLabelFor_AnEmptyTableIsTreatedAsNoTable()
        {
            // PipBuckBootstrapper hands over PipTextTable.Empty when the asset is missing, so the
            // fallback has to engage for Empty and not just for null.
            Assert.That(PipBuckPages.TabLabelFor(2, PipTextTable.Empty), Is.EqualTo("Inventory"));
            Assert.That(PipBuckPages.TabLabelFor(6, PipTextTable.Empty), Is.EqualTo("MED"));
        }

        [Test]
        public void SubPageName_UsesTheDocumentAndCorrectsTwoOfThePortsWords()
        {
            // The port's own page enum calls sub-page 3 "Aid" and 5 "Ammo" — its reading of the AS3
            // internals. The document says Equipment and Ammunition, and it is the authority.
            Assert.That(PipInventoryList.SubPageName(3), Is.EqualTo("Aid"), "the pre-import word.");
            Assert.That(PipInventoryList.SubPageName(3, Shipped), Is.EqualTo("Equipment"));
            Assert.That(PipInventoryList.SubPageName(5), Is.EqualTo("Ammo"));
            Assert.That(PipInventoryList.SubPageName(5, Shipped), Is.EqualTo("Ammunition"));

            Assert.That(PipInventoryList.SubPageName(1, Shipped), Is.EqualTo("Weapons"));
            Assert.That(PipInventoryList.SubPageName(2, Shipped), Is.EqualTo("Armor"));
            Assert.That(PipInventoryList.SubPageName(4, Shipped), Is.EqualTo("Other"));

            // Out of range is "?" in both paths — it is not a lookup failure.
            Assert.That(PipInventoryList.SubPageName(0, Shipped), Is.EqualTo("?"));
            Assert.That(PipInventoryList.SubPageName(6, Shipped), Is.EqualTo("?"));
        }

        [Test]
        public void SubPageName_WithoutADocument_FallsBackToThePortsWord()
        {
            Assert.That(PipInventoryList.SubPageName(3, null), Is.EqualTo("Aid"));
            Assert.That(PipInventoryList.SubPageName(3, PipTextTable.Empty), Is.EqualTo("Aid"));
        }

        [Test]
        public void HeaderFor_UsesTheDocumentAndKeepsTheBlankColumnsBlank()
        {
            // PipPageInv.as:68-73 — weapons. Columns 3 and 4 are blanked by the Oracle itself.
            Assert.That(PipInventoryList.HeaderFor(1, Shipped).ToArray(),
                Is.EqualTo(new[] { "key", "name", "condition", "", "", "ammunition" }));

            // PipPageInv.as:180-185 — armour has no ammotip key at all.
            Assert.That(PipInventoryList.HeaderFor(2, Shipped).ToArray(),
                Is.EqualTo(new[] { "key", "name", "condition", "", "", "" }));

            // PipPageInv.as:218-226 — the three item pages: quantity, weight, category.
            Assert.That(PipInventoryList.HeaderFor(3, Shipped).ToArray(),
                Is.EqualTo(new[] { "key", "name", "quantity", "", "weight", "category" }));
            Assert.That(PipInventoryList.HeaderFor(5, Shipped).ToArray(),
                Is.EqualTo(new[] { "key", "name", "quantity", "", "weight", "category" }));
        }

        [Test]
        public void HeaderFor_WithoutADocument_FallsBackToThePortsProse()
        {
            Assert.That(PipInventoryList.HeaderFor(1, null).ToArray(),
                Is.EqualTo(new[] { "FAV", "WEAPON", "COND", "ROUNDS", "", "AMMO" }));

            // Empty is the same case as null: the bootstrapper passes Empty when the asset is absent.
            Assert.That(PipInventoryList.HeaderFor(1, PipTextTable.Empty).ToArray(),
                Is.EqualTo(PipInventoryList.HeaderFor(1).ToArray()));
        }
    }
}
