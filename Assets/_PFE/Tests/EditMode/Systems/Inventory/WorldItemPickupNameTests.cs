using System.IO;
using NUnit.Framework;
using PFE.Tests.Editor.Common;
using PFE.UI.PipBuck;

namespace PFE.Tests.EditMode.Systems.Inventory
{
    /// <summary>
    /// Pins the name a <b>take</b> prints — the wiring half of the item-name rule, not the rule itself.
    ///
    /// <para><b>The defect this fixture exists for.</b> Taking a screwdriver printed
    /// <i>"Found: Item Name"</i> and floated <i>"Item Name"</i> over the player, while the PipBuck listed
    /// the same stack as "Screwdriver". Both were reading a name; only one of them was reading the
    /// <i>right</i> one. <see cref="PFE.Data.Definitions.ItemDefinition.displayName"/>'s C# initialiser is
    /// the literal <c>"Item Name"</c> and no importer has ever overwritten it, so <b>all 500</b> shipped
    /// item assets carry that placeholder — which means <c>WorldItemPickup</c>'s
    /// "<c>!IsNullOrEmpty(displayName) ? displayName : itemId</c>" always took the first arm. The Oracle
    /// has no name on the row at all: <c>Item.nazv</c> is <i>assigned</i> from the localisation document
    /// (<c>Item.as:206-267</c>), which <see cref="PipItemNames"/> already ports.</para>
    ///
    /// <para><b>Why the claim is split in two, and why neither half is a shortcut.</b> The pickup is a
    /// <c>MonoBehaviour</c> on a spawned <c>GameObject</c> and its name path reaches
    /// <c>PfeTextTableProvider.Shared</c> (<c>Resources.Load</c>) — so outside the editor every assertion
    /// about it dies as <c>SecurityException: ECall methods must be packaged into a system module</c>
    /// before reaching a line of test code. So:</para>
    /// <list type="number">
    ///   <item><description><b>The rule</b> — what a screwdriver is called — is asserted <i>by value</i>
    ///     against the shipped <c>text_en.xml</c>, through the exact call the pickup now makes. That is
    ///     <see cref="TheCallThePickupMakes_NamesAScrewdriverFromTheDocument"/>, and it is the string a
    ///     player reads.</description></item>
    ///   <item><description><b>The wiring</b> — that the pickup makes that call and not the old one — is
    ///     asserted by a <b>source lint</b>, the only form of guard that executes in this project's
    ///     offline harness for a component it cannot construct. See
    ///     <see cref="PickupPath_NamesItemsThroughTheDocumentAndNeverThroughTheAssetField"/>.</description></item>
    /// </list>
    ///
    /// <para><b>What the lint is worth.</b> It reads the file with comments and string literals stripped,
    /// so a comment naming the old expression cannot satisfy it — and the expression it forbids is the
    /// one that shipped the bug, quoted verbatim rather than paraphrased.</para>
    /// </summary>
    [TestFixture]
    public class WorldItemPickupNameTests
    {
        private const string PickupSourcePath = "Systems/Inventory/WorldItemPickup.cs";

        /// <summary>The old item branch, exactly as it shipped: the field wins whenever it is non-empty.</summary>
        private const string TheBug = "IsNullOrEmpty(Definition?.displayName)";

        /// <summary>The call the fixed branch makes, with the argument it must pass first.</summary>
        private const string TheFix = "PipItemNames.Item(text, definition.itemId";

        // ── The wiring ────────────────────────────────────────────────────────

        [Test]
        public void PickupPath_NamesItemsThroughTheDocumentAndNeverThroughTheAssetField()
        {
            string code = SourceLint.ReadStripped(PickupSourcePath);

            // Positive: the item half delegates to the one rule the PipBuck also uses.
            Assert.That(code, Does.Contain(TheFix),
                "The take path must name an item through PipItemNames.Item — the same rule the PipBuck " +
                "row uses (Item.as:240-267). Two rules is how the message and the list disagreed.");

            // Absent control, in the same test: a lint that only searched for the new call would pass
            // on a file that kept the old expression beside it — which is exactly how a "fix" leaves
            // the bug reachable.
            Assert.That(code, Does.Not.Contain(TheBug),
                "The old expression must be gone. `!IsNullOrEmpty(Definition?.displayName) ? " +
                "Definition.displayName : ItemId` always took its first arm, because the field's C# " +
                "initialiser is the literal \"Item Name\" on all 500 shipped item assets.");

            // The placeholder itself must never be named in shipping code that produces a caption.
            Assert.That(code, Does.Not.Contain("\"Item Name\""),
                "No literal placeholder in the pickup path: it is a field default, not a name.");
        }

        [Test]
        public void WeaponPath_StillResolvesThroughTheDocumentForAVariant()
        {
            // The weapon half was already correct and must stay so: 213 weapon assets carry `""`, so a
            // variant is named from `id^N` and falls back to the literal " - II".
            string code = SourceLint.ReadStripped(PickupSourcePath);

            Assert.That(code, Does.Contain("PipItemNames.Weapon(text, weapon.weaponId, variant"),
                "Item.as:206-220 — the variant rule is the PipBuck's, not a second copy.");
        }

        // ── The rule, by value, on the shipped document ───────────────────────

        private static PipTextTable _shipped;

        private static PipTextTable Shipped
        {
            get
            {
                if (_shipped != null) return _shipped;

                string path = Path.Combine(
                    SourceLint.ProjectRoot(), "Assets", "_PFE", "Data", "Resources", "Localisation",
                    "text_en.xml");

                Assert.That(File.Exists(path), Is.True, "The localisation document is missing: " + path);

                _shipped = PipTextTable.Parse(File.ReadAllText(path));
                Assert.That(_shipped.IsEmpty, Is.False, "The document parsed to zero entries: " + path);
                return _shipped;
            }
        }

        [Test]
        public void TheCallThePickupMakes_NamesAScrewdriverFromTheDocument()
        {
            // `screwdriver` is the item the play-test picked up, and the one the tutorial popup is
            // written for. Its asset row: `legacyTip: instr`, `baseItemId:` empty, `modifierType: 0`,
            // `workStation:` empty, `displayName: Item Name`.
            string name = PipItemNames.Item(Shipped, "screwdriver", "instr", string.Empty, 0,
                string.Empty, PipItemNames.PlaceholderName);

            Assert.That(name, Is.EqualTo("Screwdriver"),
                "`<item id=\"screwdriver\"><n>` — the word the take message and the floating label must " +
                "carry, and the word the PipBuck row already carried.");
        }

        [Test]
        public void TheOldExpressionWouldHavePrintedThePlaceholder()
        {
            // The bug, reproduced without the component: this is the value the deleted branch returned
            // for every one of the 500 shipped item assets, because the field is non-empty everywhere.
            const string fromTheAsset = "Item Name";

            Assert.That(PipItemNames.IsPlaceholder(fromTheAsset), Is.True,
                "The field's value IS the placeholder — the branch's non-empty test could never fail.");

            string fromTheDocument = PipItemNames.Item(Shipped, "screwdriver", "instr", string.Empty, 0,
                string.Empty, fromTheAsset);

            Assert.That(fromTheDocument, Is.Not.EqualTo(fromTheAsset),
                "…and the document says something else, which is why the two surfaces disagreed: the " +
                "PipBuck read the document, the take read the field.");
        }

        [Test]
        public void Shipped_ItemAssetsStillCarryThePlaceholder()
        {
            // The measurement the whole fix rests on, kept live so the reason stays checkable rather
            // than becoming folklore. Directional on purpose: an importer that starts writing real
            // names would make this assertion FALSE, and that is good news, not a regression — the
            // reader would then simply agree with the data. What must never be true again is a
            // caption-producing path that reads the field without the placeholder test.
            string itemsDir = Path.Combine(
                SourceLint.ProjectRoot(), "Assets", "_PFE", "Data", "Resources", "Items");

            string[] assets = Directory.GetFiles(itemsDir, "*.asset");
            Assert.That(assets.Length, Is.GreaterThan(400),
                "The shipped item assets are missing: " + itemsDir);

            int placeholder = 0;
            foreach (string asset in assets)
            {
                foreach (string line in File.ReadAllLines(asset))
                {
                    if (!line.StartsWith("  displayName:", System.StringComparison.Ordinal)) continue;
                    if (line.Substring("  displayName:".Length).Trim() == PipItemNames.PlaceholderName)
                    {
                        placeholder++;
                    }

                    break;
                }
            }

            Assert.That(placeholder, Is.GreaterThan(assets.Length / 2),
                "Measured 500 of 500 — every shipped item asset still carries the field default, which " +
                "is why no reader may print `ItemDefinition.displayName` without PipItemNames' " +
                "placeholder test. If this ever fails, the assets were named at import time and the " +
                "comments that cite the 500 are stale.");
        }
    }
}
