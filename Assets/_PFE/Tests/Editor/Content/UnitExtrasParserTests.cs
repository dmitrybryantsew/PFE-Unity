using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using PFE.Data.Definitions;

namespace PFE.Tests.Editor.Content
{
    /// <summary>
    /// Pins <see cref="UnitExtrasParser"/> — the reader for a unit's <c>&lt;un&gt;</c> element, AS3
    /// <c>UnitZombie.as:151-165</c>.
    ///
    /// <para><b>Why these tests exist at all.</b> The parse lives in <c>PFE.Data</c> rather than inline in
    /// <c>UnitDataImporter</c> (which is in <c>PFE.Editor</c>, an assembly <c>PFE.Tests</c> does not
    /// reference) for the same reason <see cref="UnitVulnerabilityParser"/> was extracted — and the
    /// reason is sharper here. This parse has exactly one hard job: a <b>guard</b>. <c>res</c> is an
    /// overloaded attribute name whose two AS3 owners disagree about what it means, so the failure mode
    /// of getting it wrong is a <b>silent wrong write</b> on four units, not an exception.</para>
    /// </summary>
    [TestFixture]
    public class UnitExtrasParserTests
    {
        // === Real snippets from AllData.as, verbatim ===

        [Test]
        public void AZombieVariantThatDeclaresRes_Resurrects()
        {
            // AllData.as:864 — zombie7, the first of the three resurrecting variants.
            Assert.IsTrue(UnitExtrasParser.ParseCanResurrect(
                "<unit id='zombie7' xp='450' cat='3' parent='zombie'>\n" +
                "\t<un ss='7' res='1'/>\n" +
                "\t<comb hp='150' damage='30' tipdam='16'/>\n" +
                "</unit>",
                "zombie"),
                "zombie7 authors <un ss='7' res='1'/>, so AS3 UnitZombie.as:161 sets isRes.");
        }

        [Test]
        public void AZombieVariantWithAnExtrasNodeButNoRes_DoesNotResurrect()
        {
            // AllData.as:792 — zombie1 authors <un ss='1'/> and nothing else. Covers the "node present,
            // attribute absent" case, which is where a presence test that forgot to test presence would
            // go wrong.
            Assert.IsFalse(UnitExtrasParser.ParseCanResurrect(
                "<unit id='zombie1' cont='zombie' xp='150' cat='3' parent='zombie'>\n" +
                "\t<un ss='1'/>\n" +
                "</unit>",
                "zombie"));
        }

        [Test]
        public void AZombieVariantWithNoExtrasNodeAtAll_DoesNotResurrect()
        {
            // AllData.as:784 — zombie0 has no <un> node, like six of the ten variants.
            Assert.IsFalse(UnitExtrasParser.ParseCanResurrect(
                "<unit id='zombie0' cont='zombie' xp='100' cat='3' parent='zombie'>\n" +
                "\t<comb hp='50' damage='14'/>\n" +
                "</unit>",
                "zombie"));
        }

        [Test]
        public void ATrapWhoseResIsAResourceName_DoesNotResurrect()
        {
            // THE regression test for the collision this parser exists to avoid.
            //
            // AllData.as:1887 verbatim — trigcans. It declares `res` on the SAME node, with the same
            // syntax, as a zombie does. The difference is the unit's class: UnitTrigger.as:117-119 reads
            // `this.res = node0.un.@res` as a resource NAME, so 'noise' here means "disarmable with the
            // sneak skill", not "gets back up".
            //
            // All four traps — trigcans, trigridge, trigplate, triglaser — are fraction 2 and
            // <move fixed='1'/>, and all four author no `parent=`.
            Assert.IsFalse(UnitExtrasParser.ParseCanResurrect(
                "<unit id='trigcans' fraction='2'>\n" +
                "\t<un skill='sneak' res='noise'/>\n" +
                "</unit>",
                null),
                "A trap's `res` is a resource name. Reading it as a resurrection flag is the bug this " +
                "guard prevents.");
        }

        [Test]
        public void AMissingParentIdIsNotTheZombieFamily()
        {
            // The importer leaves `parentId` at its field default (null) when the unit authors no
            // `parent=`, and Unity serialises that as "". Both must be refused — and this is the path the
            // four traps actually take, since none of them authors a parent.
            const string zombieShaped = "<unit id='x' parent='zombie'>\n\t<un res='1'/>\n</unit>";

            Assert.IsFalse(UnitExtrasParser.ParseCanResurrect(zombieShaped, null));
            Assert.IsFalse(UnitExtrasParser.ParseCanResurrect(zombieShaped, ""));
        }

        [Test]
        public void TheSameResOnePayload_IsRefusedForANonZombieFamily()
        {
            // Isolates the GUARD from the VALUE. The payload is byte-identical to zombie7's; only the
            // family differs. If the second assertion ever returns true, the parser is keying off the
            // value rather than the owner, and the four traps are one data edit from being marked
            // resurrectable.
            //
            // Note the content itself says parent='zombie' while the argument says 'raider'. That is
            // deliberate: the importer reads `parent=` from the open tag and passes it, and the parser
            // must use the argument rather than re-deriving it from the body.
            const string zombiePayload = "<unit id='x' parent='zombie'>\n\t<un ss='7' res='1'/>\n</unit>";

            Assert.IsTrue(UnitExtrasParser.ParseCanResurrect(zombiePayload, "zombie"));
            Assert.IsFalse(UnitExtrasParser.ParseCanResurrect(zombiePayload, "raider"));
        }

        [Test]
        public void TheValueIsIgnored_BecauseAS3TestsPresenceOnly()
        {
            // UnitZombie.as:161 is `if(node0.un.@res.length())` — a length test, so `res='2'` and
            // `res='0'` set isRes exactly as `res='1'` does. No unit authors anything but '1', so this is
            // a RULE test on invented data, not a data test.
            Assert.IsTrue(UnitExtrasParser.ParseCanResurrect(
                "<unit id='x' parent='zombie'>\n\t<un res='2'/>\n</unit>", "zombie"));
            Assert.IsTrue(UnitExtrasParser.ParseCanResurrect(
                "<unit id='x' parent='zombie'>\n\t<un res='0'/>\n</unit>", "zombie"));
        }

        [Test]
        public void TheOpenTagFormIsReadToo()
        {
            // All 52 <un> nodes in AllData.as are self-closing, but AS3 is E4X, where `<un>` and `<un/>`
            // are the same node. The `\b` anchor admits both; a `<un\s` pattern would not.
            Assert.IsTrue(UnitExtrasParser.ParseCanResurrect(
                "<unit id='x' parent='zombie'>\n\t<un res='1'></un>\n</unit>", "zombie"));
        }

        [Test]
        public void AResAttributeOnTheUnitTagIsNotMistakenForTheExtrasNode()
        {
            // The `\b` in `<un\b`: there is no word boundary between `n` and `i`, so `<unit …>` cannot
            // match. Every unit block STARTS with its own open tag, so a pattern without that anchor
            // would be scanning the open tag of all 148 units.
            //
            // No unit authors `res` on its open tag, so the payload is invented on purpose — it is the
            // only shape that can tell the two patterns apart.
            Assert.IsFalse(UnitExtrasParser.ParseCanResurrect(
                "<unit id='x' res='1' parent='zombie'>\n\t<comb hp='50'/>\n</unit>", "zombie"));
        }

        [Test]
        public void ASuffixedAttributeNameIsNotMistakenForRes()
        {
            // The `(?:^|\s)` anchor. No unit authors `xres`, but the whole point of anchoring is that a
            // future one cannot be read as `res`.
            Assert.IsFalse(UnitExtrasParser.ParseCanResurrect(
                "<unit id='x' parent='zombie'>\n\t<un xres='1'/>\n</unit>", "zombie"));
        }

        [Test]
        public void ContentWithoutAnExtrasNodeIsNotAnError()
        {
            Assert.IsFalse(UnitExtrasParser.ParseCanResurrect(null, "zombie"));
            Assert.IsFalse(UnitExtrasParser.ParseCanResurrect("", "zombie"));
            Assert.IsFalse(UnitExtrasParser.ParseCanResurrect(
                "<unit id='x' parent='zombie'></unit>", "zombie"));
        }

        // === The real file, when it is there ===

        [Test]
        public void TheRealAllDataResurrectsExactlyTheThreeAuthoredVariants()
        {
            string path = FindAllDataAs();
            if (path == null)
            {
                Assert.Ignore(
                    "AllData.as is not next to this project (it is an untracked working copy, and the " +
                    "tracked oracle lives outside the repository). The shape tests above still ran; " +
                    "this census did not.");
                return;
            }

            string source = File.ReadAllText(path);

            // The SHIPPED slicer, not a private regex. The census has to describe the same slices the
            // importer hands the parser, or it describes a program nobody runs.
            Dictionary<string, string> nodes = UnitNodeExtractor.ExtractAllNodes(source);

            var candidates = new List<string>();   // declares `res`, whatever the family
            var accepted = new List<string>();     // what the parser answers

            foreach (KeyValuePair<string, string> node in nodes)
            {
                Match openTag = Regex.Match(node.Value, @"<unit\b[^>]*>");
                Match parent = openTag.Success
                    ? Regex.Match(openTag.Value, @"(?:^|\s)parent='([^']+)'")
                    : Match.Empty;
                string parentId = parent.Success ? parent.Groups[1].Value : null;

                Match un = Regex.Match(node.Value, @"<un\b([^>]*)>");
                if (un.Success && Regex.IsMatch(un.Groups[1].Value, @"(?:^|\s)res='"))
                    candidates.Add(node.Key);

                if (UnitExtrasParser.ParseCanResurrect(node.Value, parentId))
                    accepted.Add(node.Key);
            }

            // 7 candidates, 3 accepted — and the gap IS the guard's work. That is what makes this census
            // able to fail in BOTH directions: a pattern that stopped matching takes the candidates to 0,
            // and a guard that was deleted takes the accepted count to 7. Asserting only "3 resurrect"
            // would pass on a parser that never matches anything.
            Assert.AreEqual(7, candidates.Count,
                "Seven units declare `res` on their <un> node: zombie7/8/9 plus the four traps " +
                "trigcans/trigridge/trigplate/triglaser. Got: " + string.Join(", ", candidates));
            CollectionAssert.AreEquivalent(
                new[] { "zombie7", "zombie8", "zombie9", "trigcans", "trigridge", "trigplate", "triglaser" },
                candidates);

            CollectionAssert.AreEquivalent(new[] { "zombie7", "zombie8", "zombie9" }, accepted,
                "Only the three zombie variants resurrect. A trap here means `res` is being read as a " +
                "resurrection flag on a unit whose controller reads it as a resource name.");

            // Name the four refusals individually, so a failure says WHICH trap leaked rather than only
            // that a count moved.
            foreach (string trap in new[] { "trigcans", "trigridge", "trigplate", "triglaser" })
            {
                Assert.IsTrue(nodes.ContainsKey(trap), trap + " must exist in AllData.as.");
                Assert.IsFalse(accepted.Contains(trap),
                    trap + " declares `res` as a RESOURCE NAME (UnitTrigger.as:117-119). It must not be " +
                    "marked as resurrecting.");
            }
        }

        /// <summary>
        /// Looks for <c>AllData.as</c> beside the project. Returns <c>null</c> when it is not there —
        /// the caller then ignores rather than fails, because its absence is an environment fact.
        /// </summary>
        private static string FindAllDataAs()
        {
            foreach (string root in CandidateProjectRoots())
            {
                string[] candidates =
                {
                    Path.Combine(root, "AllData.as"),
                    Path.Combine(root, "pfe", "scripts", "fe", "AllData.as"),
                    Path.Combine(root, "..", "pfe", "scripts", "fe", "AllData.as"),
                };

                foreach (string candidate in candidates)
                    if (File.Exists(candidate))
                        return Path.GetFullPath(candidate);
            }

            return null;
        }

        /// <summary>
        /// Project roots to look in: the working directory first, then Unity's.
        ///
        /// <para><b>Why the working directory is tried first.</b> <c>Application.dataPath</c> is a Unity
        /// <b>ECall</b>, and a host that cannot JIT an ECall fails the <i>whole method that mentions
        /// it</i>. The working directory is the project root in both venues and costs no ECall, so the
        /// census can run offline.</para>
        /// </summary>
        private static IEnumerable<string> CandidateProjectRoots()
        {
            var roots = new List<string> { Directory.GetCurrentDirectory() };

            string unityRoot = null;
            try
            {
                unityRoot = UnityProjectRoot();
            }
            catch (Exception)
            {
                // No Unity runtime (offline host): the working directory is the whole search.
            }

            if (!string.IsNullOrEmpty(unityRoot))
                roots.Add(unityRoot);

            return roots;
        }

        /// <summary>
        /// The project root according to Unity. <b>This is the only method that mentions
        /// <c>Application.dataPath</c></b>, so it is the only one an ECall-less host cannot JIT.
        /// </summary>
        private static string UnityProjectRoot()
            => Directory.GetParent(Application.dataPath).FullName;
    }
}
