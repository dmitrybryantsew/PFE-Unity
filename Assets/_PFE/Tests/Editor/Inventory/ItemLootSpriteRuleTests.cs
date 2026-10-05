using System.Collections.Generic;
using NUnit.Framework;
using PFE.Data.Definitions;

namespace PFE.Tests.EditMode.Systems.Inventory
{
    /// <summary>
    /// Pins the loot-sprite resolution against AS3 <c>fe/loc/Loot.as:85-220</c>.
    ///
    /// <para><b>Why this needs pinning.</b> Every branch of the rule is silent when it is wrong: an
    /// item that resolves to the wrong frame, or to the wrong symbol, still produces a sprite — just
    /// the wrong one — so nothing goes red and the icon only looks "a bit off" in play. The two
    /// mistakes that are easiest to make and hardest to notice are both covered below: mapping the
    /// fallback from the <c>Item.as</c> constant list rather than from the <c>catch</c> block that
    /// actually implements it (<c>compm</c>), and treating the <c>scheme</c> re-point as a fallback
    /// when the oracle makes it an override.</para>
    ///
    /// <para><b>Every "falls to frame 1" assertion is paired with a control that reaches a real
    /// frame.</b> A rule that returned frame 1 unconditionally would satisfy all of them, and the
    /// symptom — every item wearing the same default icon — is precisely the class of bug this
    /// feature exists to remove.</para>
    /// </summary>
    [TestFixture]
    public class ItemLootSpriteRuleTests
    {
        // A small stand-in for the real clips. The labels chosen are ones the export actually carries
        // (visualItem frame 23 = "antidote", visualAmmo frame 13 = "p50mg"), so a reader can compare
        // these against the export without re-deriving anything.
        static readonly Dictionary<string, int> VisualItem = new()
        {
            { "antidote", 23 },
            { "stimpak", 40 },
            { "scheme", 9 },
            { "food", 10 },
            { "paint", 8 },
            { "key", 7 },
            { "compa", 3 },
            { "compw", 4 },
            { "compe", 5 },
            { "compp", 6 },
        };

        static readonly Dictionary<string, int> VisualAmmo = new()
        {
            { "p50mg", 13 },
            { "p10", 2 },
            { "rocket", 16 },
        };

        static ItemLootSprite Resolve(
            string id, string tip, string baseLabel = null, bool hasVis = false,
            Dictionary<string, int> items = null, Dictionary<string, int> ammo = null)
            => ItemLootSpriteRule.Resolve(id, tip, baseLabel, hasVis,
                items ?? VisualItem, ammo ?? VisualAmmo);

        // ── the fallback table, read from the catch block and not the constant list ──

        [Test]
        public void FallbackLabels_MatchTheOracleCatchBlock()
        {
            // Loot.as:173-200, one `else if` per branch, in order.
            Assert.AreEqual("compa", ItemLootSpriteRule.FallbackFrameLabel("compa"));
            Assert.AreEqual("compw", ItemLootSpriteRule.FallbackFrameLabel("compw"));
            Assert.AreEqual("compe", ItemLootSpriteRule.FallbackFrameLabel("compe"));
            Assert.AreEqual("compp", ItemLootSpriteRule.FallbackFrameLabel("compp"));
            Assert.AreEqual("key", ItemLootSpriteRule.FallbackFrameLabel("key"));
            Assert.AreEqual("paint", ItemLootSpriteRule.FallbackFrameLabel("paint"));
            Assert.AreEqual("food", ItemLootSpriteRule.FallbackFrameLabel("food"));
        }

        [Test]
        public void CompM_HasNoFallback_DespiteHavingAConstant()
        {
            // The trap. Item.as:43 declares L_COMPM = "compm", so the constant list suggests compm is
            // one of the fallback family — but Loot.as's catch block never tests it, and its final
            // `else` sends a compm item to frame 1. A rule derived from the constants would hand every
            // material component the wrong (but plausible) icon.
            Assert.IsNotNull(ItemLootSpriteRule.FallbackFrameLabel(ItemLootSpriteRule.TipCompP),
                "positive control: compP IS a fallback");
            Assert.IsNull(ItemLootSpriteRule.FallbackFrameLabel("compm"), "compm must NOT be a fallback");
        }

        [Test]
        public void TipsOutsideTheFamily_HaveNoFallback()
        {
            // Positive control first: a tip that DOES have one.
            Assert.IsNotNull(ItemLootSpriteRule.FallbackFrameLabel("food"), "positive control");

            foreach (string tip in new[]
                     {
                         "med", "him", "pot", "book", "equip", "impl", "art", "spec", "stuff",
                         "instr", "note", "spell", "money", "valuables", "sphera", "compm", "", null,
                     })
            {
                Assert.IsNull(ItemLootSpriteRule.FallbackFrameLabel(tip), $"tip '{tip}' has no fallback");
            }
        }

        // ── arm 3: ammunition (Loot.as:139-162) ──────────────────────────────

        [Test]
        public void Ammo_PrefersTheBaseLabel()
        {
            var r = Resolve("p50mg_box", "a", baseLabel: "p50mg");

            Assert.AreEqual(ItemLootSpriteRule.VisualAmmoSymbol, r.SymbolName);
            Assert.AreEqual(ItemLootSpriteSource.VisualAmmo, r.Source);
            Assert.AreEqual(13, r.FrameNumber, "xml.@base='p50mg' is tried before the id");
        }

        [Test]
        public void Ammo_FallsBackToTheIdLabel_WhenThereIsNoBase()
        {
            var r = Resolve("p10", "a");

            Assert.AreEqual(ItemLootSpriteRule.VisualAmmoSymbol, r.SymbolName);
            Assert.AreEqual(2, r.FrameNumber, "gotoAndStop(item.id)");
        }

        [Test]
        public void Ammo_FallsToFrameOne_WhenNeitherLabelExists()
        {
            // Control: the same shape of call DOES reach a real frame.
            Assert.AreEqual(13, Resolve("p50mg_box", "a", baseLabel: "p50mg").FrameNumber, "positive control");

            var r = Resolve("nosuchammo", "a");
            Assert.AreEqual(ItemLootSpriteRule.VisualAmmoSymbol, r.SymbolName);
            Assert.AreEqual(ItemLootSpriteRule.FallbackFrame, r.FrameNumber, "catch → gotoAndStop(1)");
        }

        [Test]
        public void Ammo_IgnoresABaseLabelTheClipDoesNotHave_AndDoesNotRetryTheId()
        {
            // `p10` IS a visualAmmo label (frame 2), so a rule that "helpfully" retried the id after a
            // failed @base would return 2 here. The oracle cannot: the id lookup lives in the `else`
            // arm that `@base.length()` skipped, and the catch goes straight to frame 1.
            Assert.AreEqual(2, Resolve("p10", "a").FrameNumber, "positive control: the id label resolves");

            var r = Resolve("p10", "a", baseLabel: "not_a_label");

            Assert.AreEqual(ItemLootSpriteRule.VisualAmmoSymbol, r.SymbolName);
            Assert.AreEqual(ItemLootSpriteRule.FallbackFrame, r.FrameNumber,
                "a bad @base must land on frame 1, not fall through to the id label");
        }

        [Test]
        public void Ammo_UsesVisualAmmo_NotVisualItem()
        {
            var r = Resolve("antidote", "a");

            Assert.AreEqual(ItemLootSpriteRule.VisualAmmoSymbol, r.SymbolName,
                "an ammo item named like a visualItem label must still use visualAmmo");
            Assert.AreEqual(ItemLootSpriteSource.VisualAmmo, r.Source);
        }

        // ── arm 2: explosives (Loot.as:125-138) ──────────────────────────────

        [Test]
        public void Explosive_UsesItsOwnSymbol_AtFrameOne()
        {
            var r = Resolve("bomb", "e", hasVis: true);

            Assert.AreEqual("visbomb", r.SymbolName);
            Assert.AreEqual(ItemLootSpriteSource.PerItemVis, r.Source);
            Assert.AreEqual(1, r.FrameNumber, "infIco.stop() halts on frame 1");
        }

        [Test]
        public void Explosive_WithoutItsOwnSymbol_UsesVisualAmmo()
        {
            var r = Resolve("bomb", "e", hasVis: false);

            Assert.AreEqual(ItemLootSpriteRule.VisualAmmoSymbol, r.SymbolName);
            Assert.AreEqual(ItemLootSpriteSource.VisualAmmo, r.Source);
        }

        // ── arm 1: held weapons (Loot.as:85-124) ─────────────────────────────

        [Test]
        public void Weapon_UsesItsOwnSymbol()
        {
            var r = Resolve("p10mm", "weapon", hasVis: true);

            Assert.AreEqual("visp10mm", r.SymbolName);
            Assert.AreEqual(ItemLootSpriteSource.PerItemVis, r.Source);
        }

        [Test]
        public void Weapon_WithoutItsOwnSymbol_IsUnresolved_NotSilentlyFrameOne()
        {
            var r = Resolve("p10mm", "weapon", hasVis: false);

            Assert.IsFalse(r.IsResolved, "a missing weapon symbol must be reported, not guessed at");
            Assert.IsNotEmpty(r.Detail);
        }

        // ── arm 4: the general branch (Loot.as:163-220) ──────────────────────

        [Test]
        public void General_UsesTheIdLabelWhenItExists()
        {
            var r = Resolve("antidote", "med");

            Assert.AreEqual(ItemLootSpriteRule.VisualItemSymbol, r.SymbolName);
            Assert.AreEqual(ItemLootSpriteSource.VisualItem, r.Source);
            Assert.AreEqual(23, r.FrameNumber);
        }

        [Test]
        public void General_UsesTheTipFallback_WhenTheIdIsNotALabel()
        {
            // "foodbar" is not a visualItem label, so the catch block's tip arm decides.
            var r = Resolve("foodbar", "food");

            Assert.AreEqual(ItemLootSpriteRule.VisualItemSymbol, r.SymbolName);
            Assert.AreEqual(10, r.FrameNumber, "tip 'food' → label 'food'");
        }

        [Test]
        public void General_FallsToFrameOne_WhenTheTipHasNoFallback()
        {
            // Control: the same shape of call reaches a real frame.
            Assert.AreEqual(10, Resolve("foodbar", "food").FrameNumber, "positive control");

            var r = Resolve("someimplant", "impl");

            Assert.AreEqual(ItemLootSpriteRule.VisualItemSymbol, r.SymbolName);
            Assert.AreEqual(ItemLootSpriteRule.FallbackFrame, r.FrameNumber);
        }

        [Test]
        public void General_UsesFrameOne_WhenTheFallbackLabelIsMissingFromTheClip()
        {
            // A tip whose fallback label the clip lacks must degrade to frame 1, not to a null symbol.
            var r = Resolve("somekey", "key", items: new Dictionary<string, int> { { "antidote", 23 } });

            Assert.AreEqual(ItemLootSpriteRule.VisualItemSymbol, r.SymbolName);
            Assert.AreEqual(ItemLootSpriteRule.FallbackFrame, r.FrameNumber);
        }

        // ── the scheme override (Loot.as:206-210) ────────────────────────────

        [Test]
        public void Scheme_OverridesAMatchingIdLabel()
        {
            // "antidote" IS a visualItem label, so the try/catch would land on frame 23 — but the
            // scheme re-point runs AFTER the catch, so frame 9 wins. This is the assertion that fails
            // if the override is modelled as a fallback instead.
            var r = Resolve("antidote", "scheme");

            Assert.AreEqual(ItemLootSpriteRule.VisualItemSymbol, r.SymbolName);
            Assert.AreEqual(9, r.FrameNumber, "the scheme override must beat a matching id label");
        }

        [Test]
        public void Scheme_OverridesWhenTheIdIsNotALabel()
        {
            var r = Resolve("quest_note", "scheme");

            Assert.AreEqual(9, r.FrameNumber);
        }

        [Test]
        public void Scheme_KeepsItsFrame_WhenTheClipHasNoSchemeLabel()
        {
            var r = Resolve("antidote", "scheme",
                items: new Dictionary<string, int> { { "antidote", 23 } });

            Assert.AreEqual(23, r.FrameNumber, "no 'scheme' label → the pre-override frame stands");
            Assert.IsTrue(r.IsResolved);
        }

        // ── the three arms pick three different symbols ──────────────────────

        [Test]
        public void TheThreeArms_PickThreeDifferentSymbols()
        {
            Assert.AreEqual("visualItem", Resolve("antidote", "med").SymbolName);
            Assert.AreEqual("visualAmmo", Resolve("antidote", "a").SymbolName);
            Assert.AreEqual("visantidote", Resolve("antidote", "e", hasVis: true).SymbolName);
        }

        // ── robustness ───────────────────────────────────────────────────────

        [Test]
        public void EmptyId_IsUnresolved()
        {
            foreach (string id in new[] { null, "" })
            {
                var r = Resolve(id, "med");
                Assert.IsFalse(r.IsResolved, $"id '{id}' cannot resolve");
            }
        }

        [Test]
        public void MissingLabelMaps_DegradeToFrameOne_WithoutThrowing()
        {
            // A run with no SWF path has no label data at all. That must produce frame 1, not a throw.
            var r = ItemLootSpriteRule.Resolve("antidote", "med", null, false, null, null);

            Assert.IsTrue(r.IsResolved);
            Assert.AreEqual(ItemLootSpriteRule.VisualItemSymbol, r.SymbolName);
            Assert.AreEqual(ItemLootSpriteRule.FallbackFrame, r.FrameNumber);
        }

        [Test]
        public void PerItemSymbolName_IsVisPlusId()
        {
            Assert.AreEqual("visbomb", ItemLootSpriteRule.PerItemSymbolName("bomb"));
            Assert.AreEqual("visp10mm", ItemLootSpriteRule.PerItemSymbolName("p10mm"));
            Assert.IsNull(ItemLootSpriteRule.PerItemSymbolName(null));
        }

        [Test]
        public void SymbolNames_MatchTheExportSymbolTable()
        {
            // pfe/symbolClass/symbols.csv: 4356;"visualItem", 3737;"visualAmmo".
            Assert.AreEqual("visualItem", ItemLootSpriteRule.VisualItemSymbol);
            Assert.AreEqual("visualAmmo", ItemLootSpriteRule.VisualAmmoSymbol);
            Assert.AreEqual("vis", ItemLootSpriteRule.VisPrefix);
        }
    }
}
