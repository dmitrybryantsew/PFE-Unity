using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PFE.Systems.Combat;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Entities.Units
{
    /// <summary>
    /// The <b>art wiring</b> for the enclave commanders: that <c>bossencl.asset</c> actually carries the
    /// two extra sheets <see cref="BossEnclRules.BlitIdFor"/> names, and that every cell reference in
    /// those grids resolves to a real sprite.
    ///
    /// <para><b>Why this is a lint and not a behavioural test.</b> The consumer —
    /// <c>BossEnclController.ApplyCommanderSheet</c> → <c>UnitAnimator.SetSheetOverride</c> — needs a
    /// <c>GameObject</c>, a <c>SpriteRenderer</c> and a <c>ScriptableObject</c> row, so outside the editor
    /// it throws <c>ECall</c> before reaching an assertion. The failure it would catch, though, is not in
    /// the code: it is a sheet id the row does not carry, or a <c>{fileID, guid}</c> pair pointing at a
    /// sprite the sheet's <c>.meta</c> does not define. Both are readable from disk, and both would
    /// otherwise surface only as "the commander is invisible" in a play-test.</para>
    ///
    /// <para><b>What a broken pair looks like.</b> The three sheets and their metas were authored by a
    /// generator standing in for an importer run, so nothing but this fixture connects the asset's ids to
    /// the metas' ids. A re-rolled meta GUID with a stale asset reference, or a dropped variant entry,
    /// leaves <c>spriteSheet</c>/<c>cells</c> holding nulls — Unity draws nothing, and nothing throws.</para>
    /// </summary>
    [TestFixture]
    public class BossEnclSheetWiringTests
    {
        const string UnitAssetPath = "Assets/_PFE/Data/Resources/Units/bossencl.asset";
        const string SheetDirectory = "Assets/_PFE/Art/Units/Sheets";

        /// <summary>The row's own sheet — the body every commander had before the variants landed.</summary>
        const string PrimarySheetId = "sprEnclboss1";

        // ── Parsing ─────────────────────────────────────────────────────────

        sealed class Variant
        {
            public string SheetId;
            public readonly List<(long FileId, string Guid)> Cells = new List<(long, string)>();
            public int Columns;
            public int Rows;
        }

        // Line endings are normalised because core.autocrlf=true checkouts hand these files CRLF
        // terminators, and every regex below anchors on `$` — which then lands after a stray `\r`
        // and matches nothing.
        static string ReadAsset() => Normalize(File.ReadAllText(Path.Combine(SourceLint.ProjectRoot(), UnitAssetPath)));

        static string ReadMeta(string sheetId) =>
            Normalize(File.ReadAllText(Path.Combine(SourceLint.ProjectRoot(), SheetDirectory, sheetId + ".png.meta")));

        static string Normalize(string text) => text.Replace("\r\n", "\n");

        /// <summary>
        /// The <c>sheetVariants:</c> block only — not the primary <c>spriteSheet:</c> list above it,
        /// which has the same line shape. Scoping matters: a parser that read both would "find" the
        /// primary as a variant and hide exactly the omission this fixture exists to catch.
        /// </summary>
        static string VariantsBlock(string assetText)
        {
            const string header = "\n  sheetVariants:";
            int start = assetText.IndexOf(header, StringComparison.Ordinal);
            Assert.GreaterOrEqual(start, 0,
                $"{UnitAssetPath} has no `sheetVariants:` block — the two extra commander bodies are " +
                "not on the row, so tr2/tr3 cannot be drawn.");

            int end = assetText.Length;
            for (int i = start + header.Length; i < assetText.Length; i++)
            {
                // A field at this indent ends the block. List items start with `  -` and are skipped.
                if (assetText[i] != '\n' || i + 3 >= assetText.Length) continue;
                if (assetText[i + 1] == ' ' && assetText[i + 2] == ' ' &&
                    char.IsLetter(assetText[i + 3]))
                {
                    end = i;
                    break;
                }
            }

            return assetText.Substring(start, end - start);
        }

        static List<Variant> ParseVariants(string block)
        {
            var variants = new List<Variant>();
            Variant current = null;

            foreach (string raw in block.Split('\n'))
            {
                string line = raw.TrimEnd('\r').Trim();

                if (line.StartsWith("- sheetId:", StringComparison.Ordinal))
                {
                    current = new Variant { SheetId = line.Substring("- sheetId:".Length).Trim() };
                    variants.Add(current);
                    continue;
                }

                if (current == null) continue;

                if (line.StartsWith("- {fileID:", StringComparison.Ordinal))
                {
                    Match match = Regex.Match(line, @"fileID: (-?\d+), guid: ([0-9a-f]{32})");
                    Assert.IsTrue(match.Success, "Unparseable cell reference: " + line);
                    current.Cells.Add((long.Parse(match.Groups[1].Value), match.Groups[2].Value));
                    continue;
                }

                if (line.StartsWith("columns:", StringComparison.Ordinal))
                {
                    current.Columns = int.Parse(line.Substring("columns:".Length).Trim());
                    continue;
                }

                if (line.StartsWith("rows:", StringComparison.Ordinal))
                {
                    current.Rows = int.Parse(line.Substring("rows:".Length).Trim());
                }
            }

            return variants;
        }

        static string MetaGuid(string metaText)
        {
            Match match = Regex.Match(metaText, @"^guid: ([0-9a-f]{32})$", RegexOptions.Multiline);
            Assert.IsTrue(match.Success, "sheet meta has no guid line");
            return match.Groups[1].Value;
        }

        static HashSet<long> MetaInternalIds(string metaText)
        {
            var ids = new HashSet<long>();
            // Six spaces exactly: a sprite's id sits at that depth, while the TextureImporter block
            // carries its own `internalID: 0` four spaces in. A `^\s+` pattern counts that one too and
            // reports 17 cells for a 16-cell grid.
            foreach (Match match in Regex.Matches(metaText, @"^      internalID: (-?\d+)$", RegexOptions.Multiline))
            {
                ids.Add(long.Parse(match.Groups[1].Value));
            }

            return ids;
        }

        // ── The row ─────────────────────────────────────────────────────────

        /// <summary>
        /// The negative control for every other test here: if the row had stopped drawing
        /// <c>sprEnclboss1</c>, "the variants are not the primary" would be satisfied trivially and the
        /// comparison below would prove nothing.
        /// </summary>
        [Test]
        public void TheRowStillDrawsSheetOneAsItsPrimaryBody()
        {
            Match match = Regex.Match(ReadAsset(), @"^  spriteSheetId: (\S*)$", RegexOptions.Multiline);

            Assert.IsTrue(match.Success, "bossencl.asset has no spriteSheetId");
            Assert.AreEqual(PrimarySheetId, match.Groups[1].Value,
                "AS3 authors <vis blit='sprEnclboss1'> (AllData.as:642); the two other bodies are " +
                "variants and must not become the primary");
            Assert.IsNull(BossEnclRules.BlitIdFor(1),
                "tr1 authors no blitId, so the rule must say 'keep the row's sheet'");
        }

        /// <summary>
        /// The join this fixture exists for: every sheet the rule names for a commander must be on the
        /// row. A missing one is not an exception at runtime — it is a warning and a commander drawn as
        /// its brother.
        /// </summary>
        [Test]
        public void EverySheetTheRuleNames_HasAVariantOnTheRow()
        {
            List<Variant> variants = ParseVariants(VariantsBlock(ReadAsset()));

            // Positive control on the parser: two variants are authored, and neither may be the primary.
            Assert.AreEqual(2, variants.Count,
                "expected exactly the two extra enclave bodies (sprEnclboss2, sprEnclboss3)");
            foreach (Variant variant in variants)
            {
                Assert.AreNotEqual(PrimarySheetId, variant.SheetId,
                    "the primary sheet must not be repeated as a variant");
            }

            foreach (int tr in new[] { 2, 3 })
            {
                string sheetId = BossEnclRules.BlitIdFor(tr);
                Assert.IsFalse(string.IsNullOrEmpty(sheetId), $"tr{tr} must name a sheet");

                bool found = false;
                foreach (Variant variant in variants)
                {
                    if (string.Equals(variant.SheetId, sheetId, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }
                }

                Assert.IsTrue(found,
                    $"BlitIdFor({tr}) returns '{sheetId}' but bossencl.asset carries no such variant — " +
                    "the commander would keep sprEnclboss1 and log a warning.");
            }
        }

        /// <summary>
        /// Each variant must be the <b>same grid</b> as the primary: the animator keeps every
        /// <c>BlitAnim</c> row and swaps only the pixels, so a variant with a different cell count would
        /// index the wrong cells instead of failing.
        /// </summary>
        [Test]
        public void EachVariantIsACompleteGrid_MatchingTheRowsOwnCellCount()
        {
            string assetText = ReadAsset();
            int primaryCells = Regex.Matches(assetText, @"^  - \{fileID: -?\d+, guid: [0-9a-f]{32}, type: 3\}$",
                                             RegexOptions.Multiline).Count;

            Assert.AreEqual(16, primaryCells,
                "the primary sprEnclboss1 grid is 8 columns x 2 rows (130x150 cells in a 1040x300 sheet)");

            foreach (Variant variant in ParseVariants(VariantsBlock(assetText)))
            {
                Assert.AreEqual(8, variant.Columns, variant.SheetId + " columns");
                Assert.AreEqual(2, variant.Rows, variant.SheetId + " rows");
                Assert.AreEqual(variant.Columns * variant.Rows, variant.Cells.Count,
                    variant.SheetId + " must list one cell per grid position, row-major");
            }
        }

        /// <summary>
        /// Every <c>{fileID, guid}</c> in a variant grid must resolve: the guid must be that sheet's
        /// asset guid and the fileID one of the sprite sub-assets its <c>.meta</c> defines.
        ///
        /// <para>A dangling reference here is invisible at runtime — Unity hands back a null
        /// <c>Sprite</c>, <c>Draw</c> early-returns, and the commander keeps whatever the resting frame
        /// was. That is why the ids are checked rather than the count.</para>
        /// </summary>
        [Test]
        public void EveryVariantCellResolvesToASpriteInItsSheetMeta()
        {
            foreach (Variant variant in ParseVariants(VariantsBlock(ReadAsset())))
            {
                string meta = ReadMeta(variant.SheetId);
                string guid = MetaGuid(meta);
                HashSet<long> ids = MetaInternalIds(meta);

                Assert.AreEqual(variant.Columns * variant.Rows, ids.Count,
                    variant.SheetId + ".meta must define exactly one sprite per grid cell");

                for (int i = 0; i < variant.Cells.Count; i++)
                {
                    (long fileId, string cellGuid) = variant.Cells[i];

                    Assert.AreEqual(guid, cellGuid,
                        $"{variant.SheetId} cell {i} points at guid {cellGuid}, but that sheet's meta " +
                        $"is {guid}");
                    Assert.IsTrue(ids.Contains(fileId),
                        $"{variant.SheetId} cell {i} points at fileID {fileId}, which its meta does not " +
                        "define — Unity would resolve it to a null Sprite");
                }
            }
        }

        /// <summary>
        /// The variants are different bitmaps, not aliases of the primary: three commanders that all
        /// drew <c>sprEnclboss1</c> is the state this work exists to end.
        /// </summary>
        [Test]
        public void TheVariantSheetsAreDistinctAssets()
        {
            var guids = new HashSet<string>(StringComparer.Ordinal)
            {
                MetaGuid(ReadMeta(PrimarySheetId)),
            };

            foreach (Variant variant in ParseVariants(VariantsBlock(ReadAsset())))
            {
                string guid = MetaGuid(ReadMeta(variant.SheetId));
                Assert.IsTrue(guids.Add(guid),
                    $"{variant.SheetId} shares a guid with another sheet — the swap would be a no-op");
            }

            // And the files themselves differ: same dimensions, different pixels.
            long primaryLength = new FileInfo(Path.Combine(
                SourceLint.ProjectRoot(), SheetDirectory, PrimarySheetId + ".png")).Length;

            foreach (Variant variant in ParseVariants(VariantsBlock(ReadAsset())))
            {
                long length = new FileInfo(Path.Combine(
                    SourceLint.ProjectRoot(), SheetDirectory, variant.SheetId + ".png")).Length;

                Assert.AreNotEqual(primaryLength, length,
                    $"{variant.SheetId}.png is byte-identical in size to {PrimarySheetId}.png — " +
                    "check that the right export was copied");
            }
        }
    }
}
