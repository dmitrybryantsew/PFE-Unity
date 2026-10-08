using System;
using System.IO;
using NUnit.Framework;

namespace PFE.Tests.Editor.Common
{
    /// <summary>
    /// The shared instrument for <b>source lints</b> — fixtures that assert a property of the shipping
    /// code by reading it, for invariants whose behavioural test cannot run in this project's offline
    /// harness.
    ///
    /// <para><b>Why a lint is sometimes the only executable guard.</b> The behaviour in question needs a
    /// <c>GameObject</c>, a room and a component graph, and outside the editor that throws
    /// <c>SecurityException: ECall methods must be packaged into a system module</c> before reaching a
    /// single assertion — <c>UnitAnimationStepTests</c>, <c>EnemyBrainTests</c> and
    /// <c>UnitMotorReHomeTests</c> have never run offline for exactly that reason. A lint executes
    /// everywhere, so it is the one form of guard that can actually fail in the harness. It follows
    /// <c>RngSimLintTests</c>, the first fixture in this project to pin "this file must keep this shape"
    /// by reading it.</para>
    ///
    /// <para><b>Extracted rather than copied, because the stripper is the subtle part.</b> A lint that
    /// matches raw source passes on a file whose call has been deleted but whose comment still names it —
    /// the strongest form of a green test that means nothing. Getting that right once, with its own
    /// positive control, is worth more than getting it right twice by hand.</para>
    /// </summary>
    internal static class SourceLint
    {
        /// <summary>
        /// Reads a source file under <c>Assets/_PFE</c> and removes comments and string literals before
        /// it is asserted on.
        ///
        /// <para><b>This is not tidiness — without it a lint is vacuous.</b> The methods these fixtures
        /// check carry comments that name, by hand, the very calls being checked for, because that is
        /// exactly what a reader needs to be told. A lint searching raw text would therefore pass on a
        /// file whose call had been deleted — the same shape as this project's <c>make_metas.py</c>
        /// failing closed against a path that had moved.</para>
        /// </summary>
        /// <param name="relativePath">Path below <c>Assets/_PFE</c>, with <c>/</c> separators.</param>
        internal static string ReadStripped(string relativePath)
        {
            string fullPath = Path.Combine(
                ProjectRoot(),
                "Assets",
                "_PFE",
                relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.IsTrue(File.Exists(fullPath),
                "Lint target missing: " + fullPath + ". If the file moved, this fixture must be updated " +
                "rather than left to fail as a missing-path error that reads like a broken lint.");

            string stripped = StripCommentsAndStrings(File.ReadAllText(fullPath));

            // Positive control on the stripper itself: if it stopped working, every assertion in every
            // lint would pass against raw source and the lint would silently become a no-op. `///` is in
            // all the target files and appears nowhere in their code.
            Assert.That(stripped, Does.Not.Contain("///"),
                "The comment stripper did not strip — every assertion in this fixture would then be " +
                "satisfiable by a comment, including a comment that names the call it checks for.");

            return stripped;
        }

        /// <summary>
        /// The repository root, found by walking up from <b>this test assembly's own file</b> until a
        /// directory containing <c>Assets/_PFE</c> turns up.
        ///
        /// <para><b>Why not <c>Application.dataPath</c>.</b> That property is a Unity ECall, and an ECall
        /// throws when the test assembly is executed outside the editor — which is exactly how this
        /// project's offline harness runs its fixtures. A lint written against it cannot be executed at
        /// all, and an unexecuted guard is the thing the lints exist to avoid.
        /// <c>Assembly.Location</c> is plain reflection, so the same code works in both environments: in
        /// the editor the assembly sits in <c>Library/ScriptAssemblies/</c>, and in the harness in
        /// <c>.workbuddy-ai/tools/agentverify/wall/bin/</c> — both within a few levels of a directory
        /// holding <c>Assets/_PFE</c>.</para>
        /// </summary>
        internal static string ProjectRoot()
        {
            string dir = Path.GetDirectoryName(typeof(SourceLint).Assembly.Location);

            for (int i = 0; i < 12 && !string.IsNullOrEmpty(dir); i++)
            {
                if (Directory.Exists(Path.Combine(dir, "Assets", "_PFE")))
                {
                    return dir;
                }

                dir = Path.GetDirectoryName(dir);
            }

            Assert.Fail(
                "Could not find the project root — no ancestor of the test assembly contains Assets/_PFE. " +
                "Assembly was at: " + typeof(SourceLint).Assembly.Location);
            return null;
        }

        /// <summary>
        /// The body of a method, brace-matched from its signature — so an assertion about one method
        /// cannot be satisfied by the same call appearing in a different one.
        /// </summary>
        internal static string MethodBody(string source, string signature)
        {
            int sig = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(sig >= 0,
                $"Lint target method not found: {signature}. A rename must update the fixture that " +
                "asserts on it rather than quietly drop the method from coverage.");

            int open = source.IndexOf('{', sig);
            Assert.IsTrue(open >= 0, "No body after " + signature);

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
            }

            Assert.Fail("Unbalanced braces after " + signature);
            return null;
        }

        /// <summary>
        /// Replace every comment and string/char literal with spaces, preserving line structure so a
        /// reported offset still maps to a line. Handles the four forms this project's C# uses:
        /// <c>//</c>, <c>/* */</c>, <c>"…"</c> with escapes, and verbatim <c>@"…"</c> with doubled quotes.
        /// </summary>
        internal static string StripCommentsAndStrings(string source)
        {
            var output = new System.Text.StringBuilder(source.Length);

            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];

                // Line comment.
                if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
                {
                    while (i < source.Length && source[i] != '\n')
                    {
                        output.Append(' ');
                        i++;
                    }

                    if (i < source.Length) output.Append('\n');
                    continue;
                }

                // Block comment.
                if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                    {
                        output.Append(source[i] == '\n' ? '\n' : ' ');
                        i++;
                    }

                    i++;
                    continue;
                }

                // Verbatim string — no escapes, doubled quote is a literal quote.
                if (c == '@' && i + 1 < source.Length && source[i + 1] == '"')
                {
                    i += 2;
                    while (i < source.Length)
                    {
                        if (source[i] == '"')
                        {
                            if (i + 1 < source.Length && source[i + 1] == '"') i += 2;
                            else break;
                        }
                        else i++;
                    }

                    output.Append(' ');
                    continue;
                }

                // Regular string or char literal — escapes consume the next character.
                if (c == '"' || c == '\'')
                {
                    char quote = c;
                    i++;
                    while (i < source.Length && source[i] != quote)
                    {
                        if (source[i] == '\\') i++;
                        i++;
                    }

                    output.Append(' ');
                    continue;
                }

                output.Append(c);
            }

            return output.ToString();
        }
    }
}
