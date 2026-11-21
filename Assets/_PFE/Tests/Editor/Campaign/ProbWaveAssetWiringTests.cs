using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Campaign
{
    /// <summary>
    /// The six wave-only prob rooms must actually <b>carry</b> their <c>&lt;wave&gt;</c> payload on the
    /// shipped land assets. <c>CampaignDataParser</c> reads it correctly and <c>ProbWaveSpawner</c>
    /// consumes it, but both are downstream of the asset: Unity deserialises a missing YAML list to an
    /// empty one, so a land row without <c>waves:</c> yields <c>maxwave = 0</c> and the wave gate
    /// <c>nwave &lt; maxwave</c> reads as already satisfied — the room closes having spawned nothing.
    ///
    /// <para><b>Why a lint on the YAML rather than a runtime read.</b> <c>LandDefinition</c> is a
    /// <c>ScriptableObject</c>; loading one outside the editor throws <c>ECall</c>. The failure being
    /// guarded is a data shape, so parsing the text is both enough and the only thing this harness can
    /// do. It is the same reasoning as <c>BossEnclSheetWiringTests</c>.</para>
    ///
    /// <para><b>These counts are oracle literals</b>, from the six <c>&lt;prob id='w_*'&gt;</c> blocks in
    /// <c>GameData.as:137-570</c>: 24 waves, 128 <c>&lt;obj&gt;</c> children in total. A re-run of the
    /// Unity campaign importer must reproduce them, not wipe them.</para>
    /// </summary>
    [TestFixture]
    public sealed class ProbWaveAssetWiringTests
    {
        const string LandsDirectory = "Assets/_PFE/Data/Resources/Campaign/Lands";

        sealed class Expected
        {
            public string ProbId;
            public string Asset;
            public int[] Sizes;
        }

        static readonly Expected[] Rows =
        {
            new Expected { ProbId = "w_encl",   Asset = "random_encl.asset",   Sizes = new[] { 7, 5, 5, 5 } },
            new Expected { ProbId = "w_grif",   Asset = "random_mane.asset",   Sizes = new[] { 6, 6, 5, 4 } },
            new Expected { ProbId = "w_necros", Asset = "random_canter.asset", Sizes = new[] { 7, 7, 5, 6 } },
            new Expected { ProbId = "w_raiders",Asset = "random_plant.asset",  Sizes = new[] { 3, 5, 5, 5 } },
            new Expected { ProbId = "w_robots", Asset = "random_stable.asset", Sizes = new[] { 6, 6, 6, 5 } },
            new Expected { ProbId = "w_zombie", Asset = "random_sewer.asset",  Sizes = new[] { 4, 5, 5, 5 } },
        };

        static string Read(string asset) =>
            Normalize(File.ReadAllText(Path.Combine(SourceLint.ProjectRoot(), LandsDirectory, asset)));

        /// <summary>The shipped assets are CRLF and every needle below is LF — the same normalisation
        /// BossEnclSheetWiringTests does, for the same reason (git autocrlf must not decide this).</summary>
        static string Normalize(string text) => text.Replace("\r\n", "\n");

        /// <summary>
        /// One probRooms row, from <c>  - id: w_*</c> up to the next <c>  - id:</c> or the next
        /// two-space field key. Scoping is the whole point: a scan of the entire asset would "find"
        /// another row's waves and hide the one that is missing.
        /// </summary>
        static string RowOf(string text, string probId)
        {
            int start = text.IndexOf("  - id: " + probId + "\n", StringComparison.Ordinal);
            Assert.GreaterOrEqual(start, 0, $"no `- id: {probId}` in the asset");

            Match next = Regex.Match(text.Substring(start + 3), @"^  (?:- id: |\S)", RegexOptions.Multiline);
            int end = next.Success ? start + 3 + next.Index : text.Length;
            return text.Substring(start, end - start);
        }

        /// <summary>The <c>waves:</c> block of one row, with its own contents list as a positive control.</summary>
        static string WavesBlock(string row, string probId)
        {
            Assert.That(row, Does.Contain("    - tip: wave\n"),
                $"{probId} no longer has its `<con tip='wave'>` gate — checkAllCon would ignore the waves");

            int start = row.IndexOf("    waves:\n", StringComparison.Ordinal);
            Assert.GreaterOrEqual(start, 0,
                $"{probId} has no `waves:` block. Unity deserialises the missing list as empty, so " +
                "maxwave = 0 and the room closes without spawning anything.");
            return row.Substring(start);
        }

        /// <summary>
        /// Every shipped wave prob has four waves and the oracle's exact spawn counts.
        /// </summary>
        [Test]
        public void EveryWaveProb_CarriesFourWaves_WithTheOracleSpawnCounts()
        {
            int totalSpawns = 0;

            foreach (Expected row in Rows)
            {
                string block = WavesBlock(RowOf(Read(row.Asset), row.ProbId), row.ProbId);

                MatchCollection counts = Regex.Matches(block, @"^      spawnCount: (\d+)$", RegexOptions.Multiline);
                Assert.AreEqual(row.Sizes.Length, counts.Count,
                    $"{row.ProbId}: GameData.as authors {row.Sizes.Length} waves");

                for (int i = 0; i < row.Sizes.Length; i++)
                {
                    Assert.AreEqual(row.Sizes[i], int.Parse(counts[i].Groups[1].Value),
                        $"{row.ProbId} wave {i}: kolEn must equal the payload the spawner walks");
                    totalSpawns += row.Sizes[i];
                }
            }

            Assert.AreEqual(128, totalSpawns, "the six wave probs author 128 <obj> children in total");
        }

        /// <summary>
        /// <c>spawnCount</c> is a claim about the list below it. Counting the <c>unitId:</c> entries
        /// per wave and comparing catches a hand-edited asset where the count and the payload drifted.
        /// </summary>
        [Test]
        public void EveryWaveSpawnCount_MatchesItsOwnObjPayload()
        {
            foreach (Expected row in Rows)
            {
                string block = WavesBlock(RowOf(Read(row.Asset), row.ProbId), row.ProbId);

                // Split on the wave item header, not on `spawns:` — each wave has exactly one of each.
                string[] waves = Regex.Split(block, @"(?=^    - hasDelay: )", RegexOptions.Multiline);
                var bodies = new List<string>();

                foreach (string wave in waves)
                {
                    if (!wave.StartsWith("    - hasDelay: ", StringComparison.Ordinal)) continue;

                    Match declared = Regex.Match(wave, @"^      spawnCount: (\d+)$", RegexOptions.Multiline);
                    Assert.IsTrue(declared.Success, $"{row.ProbId}: a wave has no spawnCount");

                    MatchCollection ids = Regex.Matches(wave, @"^      - unitId: (\S+)$", RegexOptions.Multiline);
                    Assert.AreEqual(int.Parse(declared.Groups[1].Value), ids.Count,
                        $"{row.ProbId}: spawnCount disagrees with the number of <obj> entries");
                    Assert.Greater(ids.Count, 0, $"{row.ProbId}: a wave with no bodies would never advance kolEn");

                    foreach (Match id in ids) bodies.Add(id.Groups[1].Value);
                }

                Assert.AreEqual(row.Sizes.Length, waves.Length - 1,
                    $"{row.ProbId}: unexpected number of wave items");
                Assert.IsTrue(bodies.TrueForAll(s => !string.IsNullOrEmpty(s)),
                    $"{row.ProbId}: a spawn has no unit id");
            }
        }

        /// <summary>
        /// The <c>w_raiders</c> payload verbatim from <c>GameData.as:138-162</c> — order, the
        /// <c>tr</c> tiers and the single <c>hero='1'</c> elite. This is the row the parser fixtures
        /// pin, so the asset and the parser must not drift apart.
        /// </summary>
        [Test]
        public void WRaiders_CarriesTheOraclePayloadInDocumentOrder()
        {
            string block = WavesBlock(RowOf(Read("random_plant.asset"), "w_raiders"), "w_raiders");

            string[] first = Regex.Split(block, @"(?=^    - hasDelay: )", RegexOptions.Multiline)[1]
                .Split('\n');
            var firstIds = new List<string>();
            foreach (string line in first)
            {
                if (line.StartsWith("      - unitId: ", StringComparison.Ordinal))
                    firstIds.Add(line.Substring("      - unitId: ".Length).Trim());
            }

            Assert.AreEqual(new[] { "raider", "raider", "robot" }, firstIds.ToArray(),
                "waveSpawn uses the payload order to pick enspawn markers");
            Assert.That(block, Does.Contain("      - unitId: raider\n        tr: 7\n        hero: 1\n"),
                "the fourth wave's hero='1' elite (GameData.as:161) is missing");
            Assert.That(block, Does.Contain("    - hasDelay: 0\n      delaySeconds: 0\n"),
                "the fourth wave has NO @t — hasDelay must stay 0 (Probation.as:341-344)");
        }

        /// <summary>
        /// A non-wave prob must not acquire waves: <c>maxwave</c> comes from this list, so an
        /// accidental block on a boss/box room would stop its clear from ever completing.
        /// </summary>
        [Test]
        public void NonWaveProbs_CarryNoWavesBlock()
        {
            string plant = Read("random_plant.asset");
            Assert.That(RowOf(plant, "generator"), Does.Not.Contain("    waves:"),
                "a `<con tip='unit'>` prob has no waves in GameData.as");
            Assert.That(RowOf(plant, "w_raiders"), Does.Contain("    waves:"));
        }
    }
}
