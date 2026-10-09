#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.UnitAnimation
{
    /// <summary>
    /// The lint that joins the <b>oracle's animation rows</b> to the <b>ids the brains ask for</b> — the
    /// check that would have caught the "we deleted an animation and called it a fix" defect mechanically.
    ///
    /// <para><b>What went wrong, precisely.</b> <c>attack</c>, <c>derg</c> and <c>super</c> are authored
    /// rows in <c>AllData.as</c> (by nine units between them), but <see cref="AnimationSet"/> had no field
    /// for them, so <see cref="UnitAnimationParser"/> dropped each row and reported it as "unmapped". A
    /// review then read the report backwards — as evidence the ids were unplayable — and the previous
    /// anim lint was extended to <i>forbid</i> brains from returning them. The scorpion's punch and the
    /// raider's flight animation were therefore removed from the port, and every guard stayed green.</para>
    ///
    /// <para><b>Why the existing lint could not see it.</b> The membership lint
    /// (<c>EnemyFamiliesAndBossesTests.Lint_EveryBrainResolveAnimState_ReturnsOnlyValidAs3IdsOrNull</c>)
    /// asks "is this id one of the eighteen names the set can hold". A dropped row makes the id
    /// <i>not</i> a member, so the lint fires — but it fires on the <b>brain</b>, which is the side that
    /// was right. Nothing ever asked whether the <i>data</i> still had the row. These two fixtures ask
    /// that question in both directions.</para>
    ///
    /// <para><b>Why it is a lint and not a behaviour test, and why that is the point.</b> Every fixture
    /// that exercises a brain allocates a <c>GameObject</c>, so outside the editor it dies with
    /// <c>SecurityException: ECall methods must be packaged into a system module</c> before reaching a
    /// single assertion — the enemy suite has never executed offline for that reason. This fixture reads
    /// text and parses it, so it runs in the plain shell harness and in the editor alike. That is the
    /// whole reason it is written this way.</para>
    ///
    /// <para><b>What is deliberately NOT asserted, and why.</b> The tempting check — "every id a brain
    /// can return must have frames <i>in that unit's own set</i>" — cannot be made sound, because one
    /// brain serves several kinds and returns ids that only some of them author. Measured against the
    /// real data: <c>BeastBrain.ResolveAnimState()</c> returns <c>attack</c> (the scorpion punch, guarded
    /// by <c>_tPunch &gt; 0</c>) and <c>fall</c> (the hellhound's settle-after-landing, guarded by
    /// <c>_kind == BeastKind.Hound</c>), while <c>rat</c> and <c>tarakan</c> author neither and
    /// <c>molerat</c> authors only <c>death</c>. A per-unit check would report three false failures on
    /// correct code. Encoding the kind dispatch into the lint would make the lint a second copy of the
    /// brains, which is exactly the thing that goes stale silently. The two checks below are what
    /// remains sound, and together they cover the defect that actually happened.</para>
    /// </summary>
    [TestFixture]
    public class UnitAnimationDataLintTests
    {
        // ── locating the oracle ──────────────────────────────────────────────────────────────
        //
        // `SourceImportPaths` already resolves this for the importers, but it lives in `PFE.Editor`
        // and `PFE.Tests.csproj` does not reference `PFE.Editor` — the same split that keeps
        // `UnitAnimationParser` in the runtime assembly so it can be tested. So the search is repeated
        // here, without the `EditorPrefs` branch, and with a bounded walk so a deep path stays cheap.

        /// <summary>The file that identifies a valid source root.</summary>
        const string RootProbeRelativePath = "pfe/scripts/fe/AllData.as";

        static string s_allDataPath;
        static bool s_searched;

        /// <summary>
        /// The oracle's <c>AllData.as</c>, or <c>null</c> when this machine has no copy of the AS3 tree
        /// next to the Unity project. Resolution order: the <c>PFE_IMPORT_ROOT</c> environment variable,
        /// then a bounded walk over the project's ancestors, their siblings and their children.
        /// </summary>
        static string AllDataPath()
        {
            if (s_searched)
            {
                return s_allDataPath;
            }

            s_searched = true;

            string envRoot = Environment.GetEnvironmentVariable("PFE_IMPORT_ROOT");
            if (!string.IsNullOrWhiteSpace(envRoot))
            {
                string candidate = Path.Combine(envRoot, "pfe", "scripts", "fe", "AllData.as");
                if (File.Exists(candidate))
                {
                    s_allDataPath = candidate;
                    return s_allDataPath;
                }
            }

            // Bounded: 3 ancestors, up to 64 children each, up to 64 grandchildren each. Mirrors
            // `SourceImportPaths`'s shape rather than re-inventing an unbounded `Directory.GetFiles`.
            string dir = SourceLint.ProjectRoot();
            for (int depth = 0; depth < 3 && !string.IsNullOrEmpty(dir); depth++)
            {
                foreach (string probe in SiblingProbes(dir))
                {
                    if (File.Exists(probe))
                    {
                        s_allDataPath = probe;
                        return s_allDataPath;
                    }
                }

                dir = Path.GetDirectoryName(dir);
            }

            return null;
        }

        static IEnumerable<string> SiblingProbes(string dir)
        {
            yield return Path.Combine(dir, "pfe", "scripts", "fe", "AllData.as");

            string[] children;
            try
            {
                children = Directory.GetDirectories(dir);
            }
            catch (Exception)
            {
                yield break;
            }

            if (children.Length > 64)
            {
                Array.Resize(ref children, 64);
            }

            foreach (string child in children)
            {
                yield return Path.Combine(child, RootProbeRelativePath);

                string[] grandchildren;
                try
                {
                    grandchildren = Directory.GetDirectories(child);
                }
                catch (Exception)
                {
                    continue;
                }

                if (grandchildren.Length > 64)
                {
                    Array.Resize(ref grandchildren, 64);
                }

                foreach (string grandchild in grandchildren)
                {
                    yield return Path.Combine(grandchild, RootProbeRelativePath);
                }
            }
        }

        /// <summary>
        /// The oracle text, or an <c>Ignore</c> naming every path that was tried. An ignored fixture is
        /// <b>not</b> a pass: the runner reports it separately, so "the oracle was absent" can never be
        /// mistaken for "the data is consistent".
        /// </summary>
        static string ReadOracle()
        {
            string path = AllDataPath();
            if (path == null)
            {
                Assert.Ignore(
                    "No copy of the AS3 oracle is reachable from this machine, so the animation rows " +
                    "cannot be checked against it. Tried PFE_IMPORT_ROOT and a bounded walk of the " +
                    "project's ancestors, siblings and their children for '" + RootProbeRelativePath +
                    "'. This fixture is not a pass when it is skipped.");
            }

            return File.ReadAllText(path);
        }

        /// <summary>Every unit node in the oracle, keyed by id, plus the raw text for the row census.</summary>
        static Dictionary<string, string> ReadUnitNodes(string content)
        {
            var duplicateIds = new List<string>();
            Dictionary<string, string> nodes = UnitNodeExtractor.ExtractAllNodes(content, duplicateIds);

            Assert.IsEmpty(duplicateIds,
                "AllData.as declares duplicate unit id(s): " + string.Join(", ", duplicateIds) +
                ". A duplicate silently overwrites the earlier node, and AS3's own " +
                "`AllData.d.unit.(@id == id)[0]` would pick the FIRST one — so the port would be reading " +
                "a different node than the game does.");

            return nodes;
        }

        // ── 1. the oracle's rows must all land in a field, and every field must be a real row ──

        /// <summary>
        /// Every <c>&lt;blit id='…'&gt;</c> row in <c>AllData.as</c> must be stored, and every id
        /// <see cref="AnimationSet"/> claims to hold must be authored by some unit. Both directions are
        /// asserted, because they fail for different reasons and each is silent on its own.
        ///
        /// <para><b>Direction A — no authored row is dropped.</b> This is the defect: <c>attack</c>,
        /// <c>derg</c> and <c>super</c> had no field, so <see cref="UnitAnimationParser"/> reported them
        /// as unmapped and the animations vanished from nine units. Nothing failed.</para>
        ///
        /// <para><b>Direction B — no field is a fiction.</b> A field with no row anywhere is a constant
        /// wearing a field's clothes: it can never be populated, so any brain that returns it animates
        /// nothing. This is the animation-set form of the <c>leavesCorpse</c> defect (a parsed field with
        /// no reader, and its mirror).</para>
        /// </summary>
        [Test]
        public void Parse_EveryAuthoredBlitRow_LandsInAField_AndEveryFieldIsAuthored()
        {
            string content = ReadOracle();
            Dictionary<string, string> nodes = ReadUnitNodes(content);

            // Control on the extraction itself: if the node regex broke, `nodes` would be empty and
            // both directions below would be vacuous rather than red.
            Assert.That(nodes.Count, Is.EqualTo(148),
                "AllData.as must yield 148 unit nodes. A different number means the extraction changed " +
                "(or the oracle file did), and every assertion below would then be measuring a subset.");

            var authored = new HashSet<string>();
            var droppedRows = new List<string>();
            int rowsRead = 0;

            foreach (KeyValuePair<string, string> node in nodes)
            {
                // One node, no family pass: this is the row census, so the family overlay would double
                // every inherited row into the count.
                UnitAnimationParser.Result parsed = UnitAnimationParser.Parse(node.Value);
                rowsRead += parsed.RowsRead;

                foreach (string unmapped in parsed.UnmappedIds)
                {
                    droppedRows.Add(node.Key + " -> " + unmapped);
                }

                // Every row must have landed somewhere: in a field, or in the no-id bucket. A row that
                // is in neither was read and thrown away.
                Assert.AreEqual(parsed.RowsRead, parsed.SetIds.Count + parsed.RowsWithoutId,
                    "Unit '" + node.Key + "' read " + parsed.RowsRead + " blit rows but accounted for " +
                    (parsed.SetIds.Count + parsed.RowsWithoutId) + " — a row was read and discarded.");

                authored.UnionWith(parsed.SetIds);
            }

            // The oracle declares 176 blit rows in total (grep -o "<blit" | wc -l). Pinned so a
            // truncated read cannot pass the two assertions below.
            Assert.That(rowsRead, Is.EqualTo(176),
                "AllData.as declares 176 <blit> rows. Read " + rowsRead + " — the census is incomplete.");

            Assert.IsEmpty(droppedRows,
                "These authored <blit> rows have no field in AnimationSet and are being DROPPED, which " +
                "deletes the animation for every unit that declares them: " + string.Join("; ", droppedRows) +
                ". Add a field for the id (and to As3Ids / Assign / GetMapped) rather than adding it to " +
                "UnitAnimationParser.KnownUnmappedIds — that list is what hid this the first time.");

            CollectionAssert.AreEquivalent(AnimationSet.As3Ids, authored,
                "AnimationSet's id list and the ids actually authored in AllData.as must be the same set. " +
                "Extra in As3Ids => a field no row can ever populate (a brain returning it animates " +
                "nothing). Missing from As3Ids => an authored row is being dropped. " +
                "As3Ids=" + AnimationSet.As3Ids.Length + ", authored=" + authored.Count + ".");

            // Positive controls: the two ids every unit's idle and locomotion depend on.
            Assert.IsTrue(authored.Contains("stay"), "control: `stay` is authored by 26 units");
            Assert.IsTrue(authored.Contains("walk"), "control: `walk` is authored by 19 units");
        }

        /// <summary>
        /// The negative control for the fixture above: it must be possible for the census to report a
        /// dropped row. Without this, a <see cref="UnitAnimationParser"/> that reported nothing (or a
        /// regex that matched nothing) would make the whole fixture vacuously green — the "a search
        /// returning 0 is a claim, not an answer" failure in its test form.
        /// </summary>
        [Test]
        public void Parse_UnmappedRow_IsStillReported_SoTheCensusCanFail()
        {
            // The real row shape, with an id that genuinely has no field.
            UnitAnimationParser.Result parsed = UnitAnimationParser.Parse("<blit id='nosuchstate' y='9' len='7'/>");

            CollectionAssert.Contains(parsed.UnmappedIds, "nosuchstate",
                "control: an id with no field must be reported, or the census above cannot fail");
            Assert.IsFalse(AnimationSet.IsMapped("nosuchstate"), "control: the id really has no field");
            Assert.IsTrue(AnimationSet.IsMapped("derg"),
                "control: `derg` really has one — it is authored by raider/slaver/zebra and was the row " +
                "that this fixture exists to keep");
        }

        // ── 2. every id a brain can ask for must exist somewhere in the oracle ────────────────

        /// <summary>
        /// Every string literal in every brain's <c>ResolveAnimState()</c> must be an id
        /// <see cref="AnimationSet"/> can hold <b>and</b> an id some unit actually authors. The first half
        /// is the existing membership lint; the second is the half that catches an invented id which
        /// happens to be a valid name for a different family.
        ///
        /// <para>The literals are read from the <b>comment-stripped</b> source but with the strings kept.
        /// Both halves of that matter: stripping comments stops a prose remark that names an id from
        /// satisfying the check, and keeping strings is what makes the ids readable at all —
        /// <see cref="SourceLint.StripCommentsAndStrings"/> erases them, and an empty literal set passes
        /// every "all literals are valid" assertion without looking at anything.</para>
        /// </summary>
        [Test]
        public void EveryBrainAnimLiteral_IsAHeldId_AndIsAuthoredBySomeUnit()
        {
            string content = ReadOracle();
            var authored = new HashSet<string>();
            foreach (string node in ReadUnitNodes(content).Values)
            {
                authored.UnionWith(UnitAnimationParser.Parse(node).SetIds);
            }

            string enemiesDir = Path.Combine(
                SourceLint.ProjectRoot(), "Assets", "_PFE", "Entities", "Enemies");
            Assert.IsTrue(Directory.Exists(enemiesDir), "Enemies directory missing at " + enemiesDir);

            string[] brainFiles = Directory.GetFiles(enemiesDir, "*Brain.cs", SearchOption.AllDirectories);
            Assert.That(brainFiles.Length, Is.GreaterThanOrEqualTo(16),
                "Expected at least 16 Brain source files under " + enemiesDir + "; found " +
                brainFiles.Length + ". A glob that stops matching makes this fixture vacuous.");

            var literal = new Regex("\"([^\"]+)\"");
            int filesWithABody = 0;
            int literalsSeen = 0;
            var problems = new List<string>();

            foreach (string file in brainFiles)
            {
                string source = SourceLint.StripCommentsKeepStrings(File.ReadAllText(file));
                string body = MethodBodyOrNull(source, "string ResolveAnimState()");
                if (body == null)
                {
                    continue;   // no override, or an abstract declaration with no body to read
                }

                filesWithABody++;

                foreach (Match match in literal.Matches(body))
                {
                    string id = match.Groups[1].Value;
                    literalsSeen++;

                    if (!AnimationSet.IsMapped(id))
                    {
                        problems.Add(Path.GetFileName(file) + " returns '" + id +
                                     "', which is not one of AnimationSet's ids");
                    }
                    else if (!authored.Contains(id))
                    {
                        problems.Add(Path.GetFileName(file) + " returns '" + id +
                                     "', which no unit in AllData.as authors");
                    }
                }
            }

            // Controls on the two halves of the reading, so neither can be satisfied by reading nothing.
            Assert.That(filesWithABody, Is.GreaterThanOrEqualTo(16),
                "Only " + filesWithABody + " brains yielded a ResolveAnimState body — the method-body " +
                "reader is broken, and an empty read would make this fixture pass without checking anything.");
            Assert.That(literalsSeen, Is.GreaterThanOrEqualTo(20),
                "Only " + literalsSeen + " anim literals were found across all brains; the literal " +
                "reader is not seeing the ids (this is the failure mode of stripping strings first).");

            // The positive control for the *authored* half: `stay` is a held id, is authored, and is the
            // one literal nearly every brain returns.
            Assert.IsTrue(authored.Contains("stay") && AnimationSet.IsMapped("stay"),
                "control: `stay` must be both held and authored");

            // The negative control for the same half.
            Assert.IsFalse(authored.Contains("nosuchstate"),
                "control: an id no unit authors must not be reported as authored");

            Assert.IsEmpty(problems,
                "These brains ask for an animation the oracle cannot supply, so the unit silently draws " +
                "nothing when it enters that state: " + string.Join("; ", problems) + ".");
        }

        /// <summary>
        /// The body of the first <c>string ResolveAnimState()</c> in <paramref name="source"/>, or
        /// <c>null</c> when there is none or it has no body.
        ///
        /// <para><b>Why not <see cref="SourceLint.MethodBody"/>.</b> That helper takes the next
        /// <c>{</c> after the signature, which is right for a method that has a body and wrong for one
        /// that does not: an <c>abstract</c> or interface declaration would make it splice in the body of
        /// whatever method follows, and the literals of an unrelated method would be checked against this
        /// one's name. The check that the next non-whitespace character is <c>{</c> is what makes the
        /// difference observable instead of silent.</para>
        /// </summary>
        static string MethodBodyOrNull(string source, string signature)
        {
            int sig = source.IndexOf(signature, StringComparison.Ordinal);
            if (sig < 0)
            {
                return null;
            }

            int open = sig + signature.Length;
            while (open < source.Length && char.IsWhiteSpace(source[open]))
            {
                open++;
            }

            if (open >= source.Length || source[open] != '{')
            {
                return null;   // abstract / interface declaration — no body to read
            }

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{')
                {
                    depth++;
                }
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return source.Substring(open, i - open + 1);
                    }
                }
            }

            return null;   // unbalanced — treat as no body rather than as a truncated one
        }
    }
}
#endif
