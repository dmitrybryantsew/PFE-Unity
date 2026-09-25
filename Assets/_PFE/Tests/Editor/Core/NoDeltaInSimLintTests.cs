namespace PFE.Tests.Editor.Core
{
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Text.RegularExpressions;
    using NUnit.Framework;
    using UnityEngine;

    /// <summary>
    /// P1 §6.1 "no-delta lint": the simulation core must not read Unity's wall clock or its RNG.
    ///
    /// <para>The rule this enforces is what makes the whole milestone testable: simulation time enters
    /// as <i>data</i> — a tick index, a step scale, an explicit delta — never as an ambient engine
    /// read. <see cref="PFE.Core.SimClock"/> and <c>PFE.Systems.Physics.TilePhysicsStepMath</c> are
    /// pure arithmetic and must therefore contain no clock access at all;
    /// <see cref="PFE.Core.SimLoop"/> is the single place allowed to sample the engine clock, and
    /// only via <c>unscaledDeltaTime</c>.</para>
    ///
    /// <para><b>Why a source scan and not an IL scan.</b> The assemblies are not loaded in a way that
    /// makes method-body inspection dependable from EditMode, and the regression being guarded
    /// against is textual — someone re-adding <c>* Time.fixedDeltaTime * 60f</c>. Comments and string
    /// literals are stripped first, so documenting the old bug does not trip the lint.
    /// <see cref="Stripper_IgnoresCommentsAndStrings"/> and
    /// <see cref="Pattern_DoesNotMatchSuffixedIdentifiers"/> exist because a broken stripper or an
    /// over-eager pattern would make every other test in this file pass vacuously.</para>
    /// </summary>
    [TestFixture]
    public class NoDeltaInSimLintTests
    {
        private const string SimClockPath = "Core/SimClock.cs";
        private const string SimLoopPath = "Core/SimLoop.cs";
        private const string StepMathPath = "Systems/Physics/TilePhysicsStepMath.cs";

        /// <summary>
        /// Matches a bare <c>Time</c> identifier followed by a member access. The leading
        /// <c>\b</c> is load-bearing: without it, <c>deltaTime.x</c> would match.
        /// </summary>
        private static readonly Regex TimeAccess =
            new Regex(@"\bTime\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

        /// <summary>Whole-word <c>Random</c>, so <c>myRandom</c> is not a false positive.</summary>
        private static readonly Regex RandomUse = new Regex(@"\bRandom\b", RegexOptions.Compiled);

        [Test]
        public void PureSimCore_DoesNotReadUnityClockOrRng()
        {
            AssertNoClockOrRng(SimClockPath);
            AssertNoClockOrRng(StepMathPath);
        }

        [Test]
        public void SimLoop_SamplesOnlyUnscaledDeltaTime()
        {
            string stripped = ReadStripped(SimLoopPath);

            var offenders = new List<string>();
            var allowed = new List<string>();
            foreach (Match match in TimeAccess.Matches(stripped))
            {
                string member = match.Groups[1].Value;
                string described = "Time." + member + " (line " + LineOf(stripped, match.Index) + ")";
                if (member == "unscaledDeltaTime")
                {
                    allowed.Add(described);
                }
                else
                {
                    offenders.Add(described);
                }
            }

            CollectionAssert.IsEmpty(
                offenders,
                "SimLoop must sample only UnityEngine.Time.unscaledDeltaTime. Scaled time would make " +
                "the tick cadence depend on Time.timeScale, and fixedDeltaTime is not the sim step. " +
                "Found: " + string.Join(", ", offenders));

            // Positive control. If this fails the pattern or the path moved, and the assertion above
            // would be passing for the wrong reason.
            CollectionAssert.IsNotEmpty(
                allowed,
                "Expected SimLoop to sample UnityEngine.Time.unscaledDeltaTime. Either the pattern no " +
                "longer matches or the clock moved out of SimLoop; both invalidate this lint.");

            AssertNoRandom(SimLoopPath, stripped);
        }

        /// <summary>
        /// Proves the stripper works. Without this, a stripper that returned the empty string would
        /// make <see cref="PureSimCore_DoesNotReadUnityClockOrRng"/> pass on a file full of deltas.
        /// </summary>
        [Test]
        public void Stripper_IgnoresCommentsAndStrings()
        {
            const string sample =
                "var a = Time.deltaTime; // Time.fixedDeltaTime\n" +
                "var b = \"Time.time\";\n" +
                "/* Time.unscaledDeltaTime */\n" +
                "var c = Time.fixedDeltaTime;";

            string stripped = StripCommentsAndStrings(sample);
            MatchCollection hits = TimeAccess.Matches(stripped);

            Assert.AreEqual(
                2,
                hits.Count,
                "Exactly the two real accesses should survive stripping. Got: " + stripped);
            Assert.AreEqual("deltaTime", hits[0].Groups[1].Value);
            Assert.AreEqual("fixedDeltaTime", hits[1].Groups[1].Value);
        }

        /// <summary>
        /// Guards the word boundaries in both patterns. <c>deltaTime.</c> and <c>myRandom</c> are
        /// ordinary identifiers and must not be reported as clock or RNG access.
        /// </summary>
        [Test]
        public void Pattern_DoesNotMatchSuffixedIdentifiers()
        {
            string stripped = StripCommentsAndStrings(
                "var x = deltaTime.y;\n" +
                "var z = myRandom.next;\n" +
                "var w = fixedDeltaTime.value;");

            Assert.IsEmpty(
                TimeAccess.Matches(stripped),
                "'deltaTime.' / 'fixedDeltaTime.' must not match the bare `Time.` pattern.");

            Assert.IsEmpty(
                RandomUse.Matches(stripped),
                "'myRandom' must not match the whole-word `Random` pattern.");
        }

        private static void AssertNoClockOrRng(string relativePath)
        {
            string stripped = ReadStripped(relativePath);

            var clockHits = new List<string>();
            foreach (Match match in TimeAccess.Matches(stripped))
            {
                clockHits.Add(
                    "Time." + match.Groups[1].Value + " (line " + LineOf(stripped, match.Index) + ")");
            }

            CollectionAssert.IsEmpty(
                clockHits,
                relativePath + " is pure simulation arithmetic and must receive time as a parameter. " +
                "Found: " + string.Join(", ", clockHits));

            AssertNoRandom(relativePath, stripped);
        }

        private static void AssertNoRandom(string relativePath, string stripped)
        {
            var hits = new List<string>();
            foreach (Match match in RandomUse.Matches(stripped))
            {
                hits.Add("line " + LineOf(stripped, match.Index));
            }

            CollectionAssert.IsEmpty(
                hits,
                relativePath + " must not touch UnityEngine.Random. Unseeded RNG makes the simulation " +
                "non-reproducible, which blocks both the determinism test and P3's seeded RNG. Found at: " +
                string.Join(", ", hits));
        }

        private static string ReadStripped(string relativePath)
        {
            string fullPath = Path.Combine(
                Application.dataPath,
                "_PFE",
                relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.IsTrue(
                File.Exists(fullPath),
                "Lint target is missing: " + fullPath + ". The lint would otherwise pass vacuously.");

            return StripCommentsAndStrings(File.ReadAllText(fullPath));
        }

        private static int LineOf(string text, int index)
        {
            int line = 1;
            int limit = Mathf.Min(index, text.Length);
            for (int i = 0; i < limit; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                }
            }

            return line;
        }

        /// <summary>
        /// Replaces comments and string/char literals with spaces while preserving length and line
        /// breaks, so match indices still map to correct line numbers.
        ///
        /// <para>Interpolated-string holes are treated as literal text, i.e. code inside
        /// <c>$"{ ... }"</c> is stripped along with the string. That is a deliberate, documented
        /// false-negative: writing a clock read inside an interpolation hole is pathological, and
        /// tracking holes would add a brace-depth state machine for no realistic gain.</para>
        /// </summary>
        private static string StripCommentsAndStrings(string source)
        {
            var output = new StringBuilder(source.Length);
            int i = 0;

            while (i < source.Length)
            {
                char c = source[i];
                char next = i + 1 < source.Length ? source[i + 1] : '\0';

                if (c == '/' && next == '/')
                {
                    while (i < source.Length && source[i] != '\n')
                    {
                        output.Append(' ');
                        i++;
                    }

                    continue;
                }

                if (c == '/' && next == '*')
                {
                    output.Append("  ");
                    i += 2;
                    while (i < source.Length &&
                           !(source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/'))
                    {
                        output.Append(source[i] == '\n' ? '\n' : ' ');
                        i++;
                    }

                    if (i < source.Length)
                    {
                        output.Append("  ");
                        i += 2;
                    }

                    continue;
                }

                if (c == '@' && next == '"')
                {
                    output.Append("  ");
                    i += 2;
                    while (i < source.Length)
                    {
                        if (source[i] == '"')
                        {
                            if (i + 1 < source.Length && source[i + 1] == '"')
                            {
                                output.Append("  ");
                                i += 2;
                                continue;
                            }

                            output.Append(' ');
                            i++;
                            break;
                        }

                        output.Append(source[i] == '\n' ? '\n' : ' ');
                        i++;
                    }

                    continue;
                }

                if (c == '"' || (c == '$' && next == '"'))
                {
                    if (c == '$')
                    {
                        output.Append(' ');
                        i++;
                    }

                    output.Append(' ');
                    i++;
                    while (i < source.Length && source[i] != '"')
                    {
                        if (source[i] == '\\' && i + 1 < source.Length)
                        {
                            output.Append("  ");
                            i += 2;
                            continue;
                        }

                        output.Append(source[i] == '\n' ? '\n' : ' ');
                        i++;
                    }

                    if (i < source.Length)
                    {
                        output.Append(' ');
                        i++;
                    }

                    continue;
                }

                if (c == '\'')
                {
                    output.Append(' ');
                    i++;
                    while (i < source.Length && source[i] != '\'')
                    {
                        if (source[i] == '\\' && i + 1 < source.Length)
                        {
                            output.Append("  ");
                            i += 2;
                            continue;
                        }

                        output.Append(' ');
                        i++;
                    }

                    if (i < source.Length)
                    {
                        output.Append(' ');
                        i++;
                    }

                    continue;
                }

                output.Append(c);
                i++;
            }

            return output.ToString();
        }
    }
}
