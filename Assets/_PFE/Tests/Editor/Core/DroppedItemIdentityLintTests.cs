using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// §10.2 gap 2, wiring side: every floor-loot record that reaches a room must carry a host-assigned
    /// id, and the only place that assigns one is <c>RoomInstance.AddDroppedItem</c>.
    ///
    /// <para><b>Why a source lint and not a test.</b> The behaviour needs a <c>RoomInstance</c>, which
    /// needs the editor — <c>RoomInstance</c> is a plain class, but its <c>Vector2</c> fields make every
    /// construction path an ECall outside the editor, so a fixture written there would never run in this
    /// project's offline harness. The rule itself is pure and is tested by
    /// <c>DroppedItemIdentityRulesTests</c>; what is left, and what actually breaks in practice, is the
    /// <i>call site</i>: a new loader or a new drop path that appends to <c>droppedItems</c> directly
    /// produces loot with no identity, and the projector then cannot express it.</para>
    ///
    /// <para><b>What "directly" means.</b> <c>droppedItems.Add(</c> is allowed in exactly one file, in
    /// exactly one method — the one that mints first. Every other production file must reach the list
    /// through <c>AddDroppedItem</c>. The save loader is the case that motivated this: it rebuilt the
    /// list wholesale, which is right for the list and wrong for identity, and it now goes through the
    /// method.</para>
    ///
    /// <para><b>The scanner is pinned, because a blind scan fails open.</b> A lint that walks a
    /// directory and finds nothing passes. So one test asserts the walk actually reached both files this
    /// fixture is about, and another runs the violation predicate over a synthetic line to prove it can
    /// go red — the same two controls <c>SimStateRecordPurityLintTests</c> carries.</para>
    /// </summary>
    [TestFixture]
    public sealed class DroppedItemIdentityLintTests
    {
        private const string RoomFile = "Systems/Map/RoomInstance.cs";
        private const string LoaderFile = "Systems/Map/Serialization/RoomStateSnapshot.cs";

        // A record appended to the list without passing the mint. `.Insert(` is included because it is
        // the obvious way to bypass `.Add(` while keeping the intent.
        private static readonly string[] DirectAppendMarkers =
        {
            "droppedItems.Add(",
            "droppedItems.Insert(",
        };

        [Test]
        public void AddDroppedItem_AssignsTheIdThroughTheRule()
        {
            string stripped = SourceLint.ReadStripped(RoomFile);
            string body = MethodBody(stripped, "public void AddDroppedItem(DroppedItemInstance item)",
                "public bool RemoveDroppedItem(DroppedItemInstance item)");

            Assert.That(body, Does.Contain("entityHash"),
                "AddDroppedItem is the one place a stack is given its identity; it no longer touches "
                + "entityHash, so records reach the projector with a zero id and the receiver rejects "
                + "the whole loot set as corrupt.");
            Assert.That(body, Does.Contain("DroppedItemIdentityRules.Resolve"),
                "The keep-or-mint decision must go through the rule the offline wall can execute, "
                + "otherwise it exists only here and no test can reach it.");
            Assert.That(body, Does.Contain("AllocateRuntimeId"),
                "Without the host allocator the rule is handed no mint and every record stays "
                + "unassigned — a green pure test and a broken game.");
        }

        [Test]
        public void NoOtherProductionFile_AppendsToTheLootListDirectly()
        {
            var offenders = new List<string>();

            foreach (string file in ProductionSources())
            {
                string stripped = SourceLint.ReadStripped(file);
                foreach (string marker in DirectAppendMarkers)
                {
                    int at = stripped.IndexOf(marker, StringComparison.Ordinal);
                    if (at < 0) continue;

                    if (file == RoomFile && marker == "droppedItems.Add(")
                    {
                        // The minting path itself. Exactly one occurrence is expected; a second one
                        // would be a record added after the mint without passing through it.
                        int second = stripped.IndexOf(marker, at + 1, StringComparison.Ordinal);
                        if (second >= 0)
                        {
                            offenders.Add(file + ": " + marker + " appears twice");
                        }
                        continue;
                    }

                    offenders.Add(file + ": " + marker);
                }
            }

            Assert.IsEmpty(offenders,
                "A record appended to droppedItems without passing AddDroppedItem carries no identity: "
                + "the projector cannot express it and the receiver refuses a zero EntityHash. Route it "
                + "through AddDroppedItem instead. Offenders: " + string.Join("; ", offenders));
        }

        [Test]
        public void TheSaveLoader_RestoresThroughAddDroppedItem()
        {
            string stripped = SourceLint.ReadStripped(LoaderFile);

            Assert.That(stripped, Does.Contain("room.AddDroppedItem(item)"),
                "The loader rebuilds the loot list wholesale from a save, which is the one path that "
                + "creates records that were never added by a pickup. It must go through AddDroppedItem "
                + "or restored loot has no id.");
        }

        // ── Controls: the scanner and the predicate must both be able to fail ────

        [Test]
        public void TheScanner_ReachesBothFilesThisFixtureIsAbout()
        {
            List<string> files = ProductionSources();

            // The walk is the fallible part: an over-eager exclusion (say, everything under
            // `Systems/Map`) would leave both assertions above scanning nothing and passing.
            Assert.That(files, Does.Contain(RoomFile), "The scan never reached the minting site.");
            Assert.That(files, Does.Contain(LoaderFile), "The scan never reached the save loader.");
            Assert.That(files.Count, Is.GreaterThan(100),
                "A scan that returns a handful of files has stopped walking the tree, and every "
                + "absence assertion in this fixture would then be vacuous.");
        }

        [Test]
        public void TheDirectAppendPredicate_FlagsADirectAppend()
        {
            const string offending = "room.droppedItems.Add(item);";
            bool flagged = false;
            foreach (string marker in DirectAppendMarkers)
            {
                if (offending.Contains(marker))
                {
                    flagged = true;
                }
            }

            Assert.IsTrue(flagged,
                "A guard that cannot fail is not a guard: the marker list no longer matches the shape "
                + "of a direct append, so the lint above would pass on the defect it exists to catch.");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Every production C# file below <c>Assets/_PFE</c>, relative to it with <c>/</c> separators,
        /// excluding the test tree. <c>SourceLint.ReadStripped</c> takes the relative form, so the walk
        /// and the reader cannot disagree about which file is being read.
        /// </summary>
        private static List<string> ProductionSources()
        {
            string root = Path.Combine(SourceLint.ProjectRoot(), "Assets", "_PFE");
            var files = new List<string>();

            foreach (string full in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string relative = full.Substring(root.Length + 1).Replace('\\', '/');
                if (relative.StartsWith("Tests/", StringComparison.Ordinal)) continue;
                files.Add(relative);
            }

            files.Sort(StringComparer.Ordinal);
            return files;
        }

        /// <summary>
        /// The text between two declarations. A whole-file `Does.Contain` would pass on a comment
        /// elsewhere in the file — and <c>SourceLint</c> strips comments, so a whole-file check would
        /// also pass on the <i>docstring</i> that names the call. Scoping is what makes the assertion
        /// about the method rather than about the file.
        /// </summary>
        private static string MethodBody(string stripped, string start, string end)
        {
            int from = stripped.IndexOf(start, StringComparison.Ordinal);
            Assert.GreaterOrEqual(from, 0,
                "Declaration not found: " + start + ". If it was renamed, this lint must be updated "
                + "rather than left to fail as a missing-anchor error that reads like a code defect.");

            int to = stripped.IndexOf(end, from + start.Length, StringComparison.Ordinal);
            Assert.Greater(to, from, "End anchor not found after " + start + ": " + end);

            return stripped.Substring(from, to - from);
        }
    }
}
