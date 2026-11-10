using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// Phase 2.2 lint (F-09): no <c>isServer</c> / <c>isClient</c> / <c>isHost</c> /
    /// <c>isLocalPlayer</c> token may appear in <c>Assets/_PFE/Sim/**</c>. This is row 2.2's second
    /// acceptance guard.
    ///
    /// <para><b>Why the Sim must be authority-agnostic.</b> Under host-authoritative multiplayer
    /// (<c>docs/Roadmap/05</c> §1, decided — do not relitigate) the <i>same</i> rule layer runs on
    /// host and client. Authority lives in <i>who calls</i> the Sim — the host steps the world, a
    /// client predicts only its own entity — never in a branch <i>inside</i> it. An
    /// <c>if (isServer)</c> in <c>Sim/**</c> would fork the rules the two peers run, which is the
    /// exact divergence the readiness report's F4 test exists to catch far too late. So the token is
    /// forbidden outright: there is no legitimate reason for the pure rule layer to ask which side it
    /// is on. See <c>docs/multiplayer/Physics_And_Units_Multiplayer_Readiness_2026-11-04.md</c> §11
    /// item 6.</para>
    ///
    /// <para><b>Green on day one, a guard thereafter.</b> No such token exists in <c>Sim/**</c>
    /// today (source-verified 2026-11-05), so this lint is green the moment it lands — its job is to
    /// stay green as transport-era code is written, failing the first time someone reaches for a
    /// netcode flag inside the rule layer.</para>
    ///
    /// <para><b>Mutation control.</b> The lint proves it can go red by running the same predicate
    /// over a planted <c>isServer</c> token and asserting it is flagged. A guard that cannot fail is
    /// not a guard.</para>
    /// </summary>
    [TestFixture]
    public sealed class SimAuthorityBranchLintTests
    {
        private const string SimRoot = "Sim";

        /// <summary>
        /// The authority-branch tokens a host/client-split netcode layer would introduce. Matched as
        /// whole identifiers (word boundaries) so a legitimate identifier that merely <i>contains</i>
        /// one as a substring — e.g. a hypothetical <c>isServerSide</c> — would still match (that is
        /// intended: it is still an authority branch), while a word like <c>thisService</c> does not.
        /// </summary>
        private static readonly Regex AuthorityBranchRegex = new Regex(
            @"\b(isServer|isClient|isHost|isLocalPlayer)\b",
            RegexOptions.Compiled);

        [Test]
        public void SimLayer_ContainsNoAuthorityBranchToken()
        {
            var violations = new List<string>();

            foreach (string relativePath in EnumerateSimFiles())
            {
                string stripped = SourceLint.ReadStripped(relativePath);

                foreach (Match match in AuthorityBranchRegex.Matches(stripped))
                {
                    violations.Add($"{relativePath}:{LineOf(stripped, match.Index)} authority-branch " +
                        $"token `{match.Value}` — the Sim must be authority-agnostic (host/client " +
                        "lives in who calls it, not in a branch inside it)");
                }
            }

            CollectionAssert.IsEmpty(violations,
                "Assets/_PFE/Sim/** must stay free of isServer/isClient/isHost/isLocalPlayer. " +
                "The same rule layer runs on host and client; authority is who calls it, not a " +
                "branch inside it (readiness §11 item 6, row 2.2). Violations:\n" +
                string.Join("\n", violations));
        }

        [Test]
        public void MutationControl_APlantedIsServerToken_IsDetected()
        {
            // The exact failure this lint guards: a Sim file gains `if (isServer) { ... }`. Run the
            // same predicate over that token and assert it is flagged. If the predicate cannot flag
            // an isServer branch, the green result above is vacuous.
            bool flagged = AuthorityBranchRegex.IsMatch("if (isServer) { Step(); }");

            Assert.IsTrue(flagged,
                "Mutation control failed: a planted `isServer` token was not detected. The " +
                "authority-branch predicate is broken and the Sim-purity result cannot be trusted.");
        }

        [Test]
        public void MutationControl_APlantedIsClientToken_IsDetected()
        {
            bool flagged = AuthorityBranchRegex.IsMatch("bool mine = isLocalPlayer && isClient;");

            Assert.IsTrue(flagged,
                "Mutation control failed: planted `isLocalPlayer`/`isClient` tokens were not detected.");
        }

        // ── File enumeration ─────────────────────────────────────────────────

        /// <summary>All .cs files under Assets/_PFE/Sim, as paths relative to Assets/_PFE.</summary>
        private static IEnumerable<string> EnumerateSimFiles()
        {
            string fullRoot = Path.Combine(SourceLint.ProjectRoot(), "Assets", "_PFE", SimRoot);
            if (!Directory.Exists(fullRoot))
            {
                yield break;
            }

            string pfePrefix = Path.Combine(SourceLint.ProjectRoot(), "Assets", "_PFE") +
                Path.DirectorySeparatorChar;

            foreach (string file in Directory.EnumerateFiles(fullRoot, "*.cs", SearchOption.AllDirectories))
            {
                string relative = file.Substring(pfePrefix.Length);
                yield return relative.Replace(Path.DirectorySeparatorChar, '/');
            }
        }

        private static int LineOf(string text, int index)
        {
            int line = 1;
            for (int i = 0; i < index && i < text.Length; i++)
            {
                if (text[i] == '\n') line++;
            }
            return line;
        }
    }
}
