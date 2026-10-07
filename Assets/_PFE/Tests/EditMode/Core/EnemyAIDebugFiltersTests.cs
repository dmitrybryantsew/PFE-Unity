using System.Collections.Generic;
using NUnit.Framework;
using PFE.Core;

namespace PFE.Tests.EditMode.Core
{
    /// <summary>
    /// The enemy-AI overlay's sub-filter: parsing, formatting, and the independence contract.
    ///
    /// <para><b>Why this is a fixture and not three asserts.</b> The filter is what stands between the
    /// console's <c>ai vision,los</c> and a screen that shows the right two things. Every failure mode
    /// here is silent and reads as a finding rather than as a bug: a typo that parses to None leaves the
    /// overlay on and drawing nothing, which is indistinguishable from "this enemy really cannot see the
    /// player" — the exact conclusion the overlay exists to support. And a copy-paste that gave two flags
    /// the same <c>1 &lt;&lt; n</c> would make one toggle silently drive another.</para>
    /// </summary>
    [TestFixture]
    public class EnemyAIDebugFiltersTests
    {
        /// <summary>Every flag, so a new member that is not covered here is itself a test failure.</summary>
        private static readonly EnemyAIDebugFilter[] EveryFlag =
        {
            EnemyAIDebugFilter.Vision,
            EnemyAIDebugFilter.Hearing,
            EnemyAIDebugFilter.CloseProximity,
            EnemyAIDebugFilter.LineOfSight,
            EnemyAIDebugFilter.Target,
            EnemyAIDebugFilter.States,
        };

        // ── Parsing ──────────────────────────────────────────────────────────

        [Test]
        public void TryParse_Empty_MeansAll()
        {
            foreach (string text in new[] { null, "", "   ", "\t" })
            {
                Assert.IsTrue(EnemyAIDebugFilters.TryParse(text, out EnemyAIDebugFilter filter, out string error),
                    $"'{text}' should parse");
                Assert.AreEqual(EnemyAIDebugFilter.All, filter);
                Assert.IsNull(error);
            }
        }

        [Test]
        public void TryParse_AllTokens_MeanAll()
        {
            foreach (string text in new[] { "all", "*", "everything", "on", "ALL", "All" })
            {
                Assert.IsTrue(EnemyAIDebugFilters.TryParse(text, out EnemyAIDebugFilter filter, out string error),
                    $"'{text}' should parse");
                Assert.AreEqual(EnemyAIDebugFilter.All, filter, $"'{text}' should mean All");
                Assert.IsNull(error);
            }
        }

        [Test]
        public void TryParse_OffTokens_MeanNone()
        {
            foreach (string text in new[] { "off", "none", "0", "false", "hide", "clear" })
            {
                Assert.IsTrue(EnemyAIDebugFilters.TryParse(text, out EnemyAIDebugFilter filter, out string error),
                    $"'{text}' should parse");
                Assert.AreEqual(EnemyAIDebugFilter.None, filter, $"'{text}' should mean None");
                Assert.IsNull(error);
            }
        }

        [Test]
        public void TryParse_EveryCanonicalName_ParsesToItsOwnFlag()
        {
            // The guard that catches "added a flag and forgot the switch case": the formatter's own
            // output must be accepted back, so the vocabulary cannot drift from the parser.
            foreach (EnemyAIDebugFilter flag in EveryFlag)
            {
                string name = EnemyAIDebugFilters.Format(flag);

                Assert.IsTrue(EnemyAIDebugFilters.TryParse(name, out EnemyAIDebugFilter parsed, out string error),
                    $"canonical name '{name}' for {flag} must parse: {error}");
                Assert.AreEqual(flag, parsed, $"'{name}' must parse back to {flag}");
            }
        }

        [Test]
        public void TryParse_IsCaseAndWhitespaceInsensitive()
        {
            Assert.IsTrue(EnemyAIDebugFilters.TryParse("Vision", out EnemyAIDebugFilter filter, out _));
            Assert.AreEqual(EnemyAIDebugFilter.Vision, filter);

            Assert.IsTrue(EnemyAIDebugFilters.TryParse("  VISION  ", out filter, out _));
            Assert.AreEqual(EnemyAIDebugFilter.Vision, filter);
        }

        [Test]
        public void TryParse_AcceptsEverySeparatorTheConsoleMightSee()
        {
            foreach (string text in new[] { "vision,los", "vision los", "vision;los", "vision+los", "vision|los" })
            {
                Assert.IsTrue(EnemyAIDebugFilters.TryParse(text, out EnemyAIDebugFilter filter, out string error),
                    $"'{text}' should parse: {error}");
                Assert.AreEqual(EnemyAIDebugFilter.Vision | EnemyAIDebugFilter.LineOfSight, filter,
                    $"'{text}' should be vision+los");
            }
        }

        [Test]
        public void TryParse_IsOrderIndependent()
        {
            EnemyAIDebugFilters.TryParse("vision,hearing,los", out EnemyAIDebugFilter a, out _);
            EnemyAIDebugFilters.TryParse("los,hearing,vision", out EnemyAIDebugFilter b, out _);
            EnemyAIDebugFilters.TryParse("hearing,los,vision", out EnemyAIDebugFilter c, out _);

            Assert.AreEqual(a, b);
            Assert.AreEqual(a, c);
        }

        [Test]
        public void TryParse_Senses_ExpandsToTheThreeSenses()
        {
            // The request that produced this overlay was "visualize what the AI sees, hears, etc", so
            // `senses` is the primary spelling for the group — and it is the only token that expands to
            // more than one flag, which is the case a naive one-token-one-flag parser gets wrong.
            Assert.IsTrue(EnemyAIDebugFilters.TryParse("senses", out EnemyAIDebugFilter filter, out string error),
                error);

            Assert.AreEqual(
                EnemyAIDebugFilter.Vision | EnemyAIDebugFilter.Hearing | EnemyAIDebugFilter.CloseProximity,
                filter);

            Assert.IsFalse(EnemyAIDebugFilters.Has(filter, EnemyAIDebugFilter.LineOfSight),
                "`senses` must NOT drag in the LOS ray — that is a drawing, not a sense, and folding it " +
                "in would make the one flag that explains a failed detection impossible to switch off");

            Assert.IsFalse(EnemyAIDebugFilters.Has(filter, EnemyAIDebugFilter.States));
        }

        [Test]
        public void TryParse_UnknownToken_FailsAndNamesTheToken()
        {
            Assert.IsFalse(EnemyAIDebugFilters.TryParse("visionn", out EnemyAIDebugFilter filter, out string error));

            Assert.AreEqual(EnemyAIDebugFilter.None, filter, "a failed parse must not leave flags on");
            StringAssert.Contains("visionn", error);
            StringAssert.Contains("Unknown", error);
        }

        [Test]
        public void TryParse_UnknownTokenAmongGoodOnes_DoesNotPartiallySucceed()
        {
            // The nasty case: "vision,visionn" must not silently turn vision on and drop the typo. That
            // reads as "the overlay is on and the enemy sees nothing", which is exactly the false
            // finding this tool exists to prevent.
            Assert.IsFalse(EnemyAIDebugFilters.TryParse("vision,visionn", out EnemyAIDebugFilter filter, out string error));

            Assert.AreEqual(EnemyAIDebugFilter.None, filter);
            StringAssert.Contains("visionn", error);
        }

        [Test]
        public void TryParse_NeverReturnsAPartiallyAccumulatedFilterOnFailure()
        {
            // Walk every unknown-token position, so the "reset to None on failure" line cannot be
            // removed without a failure. This is the property, not one example of it.
            string[] bad =
            {
                "zzz", "vision,zzz", "zzz,vision", "vision,hearing,zzz", "vision,zzz,hearing",
            };

            foreach (string text in bad)
            {
                Assert.IsFalse(EnemyAIDebugFilters.TryParse(text, out EnemyAIDebugFilter filter, out string error),
                    $"'{text}' should fail");
                Assert.AreEqual(EnemyAIDebugFilter.None, filter,
                    $"'{text}' left {filter} on — a failed parse must be total");
                StringAssert.Contains("zzz", error);
            }
        }

        // ── Aliases ──────────────────────────────────────────────────────────

        [Test]
        public void Aliases_ResolveToTheExpectedFlag()
        {
            AssertAliases(EnemyAIDebugFilter.Vision, "vision", "sight", "see", "cone");
            AssertAliases(EnemyAIDebugFilter.Hearing, "hearing", "hear", "ear", "noise");
            AssertAliases(EnemyAIDebugFilter.CloseProximity, "close", "proximity", "detecting", "near");
            AssertAliases(EnemyAIDebugFilter.LineOfSight, "los", "lineofsight", "ray", "raycast");
            AssertAliases(EnemyAIDebugFilter.Target, "target", "lastknown", "last");
            AssertAliases(EnemyAIDebugFilter.States, "states", "state", "labels", "label");
        }

        private static void AssertAliases(EnemyAIDebugFilter expected, params string[] aliases)
        {
            foreach (string alias in aliases)
            {
                Assert.IsTrue(EnemyAIDebugFilters.TryParse(alias, out EnemyAIDebugFilter filter, out string error),
                    $"alias '{alias}' should be recognised: {error}");
                Assert.AreEqual(expected, filter, $"alias '{alias}' should mean {expected}");
            }
        }

        // ── Independence: one bit each ───────────────────────────────────────

        [Test]
        public void EveryFlag_IsAnIndependentBit()
        {
            // A copy-paste that gave two flags the same 1 << n would show up here and nowhere else —
            // and its symptom in play is a toggle that silently drives a second drawing.
            foreach (EnemyAIDebugFilter flag in EveryFlag)
            {
                Assert.AreNotEqual(0, (int)flag, $"{flag} must not be zero");

                int bits = 0;
                for (int v = (int)flag; v != 0; v &= v - 1) bits++;
                Assert.AreEqual(1, bits, $"{flag} must occupy exactly one bit");
            }

            var seen = new HashSet<int>();
            foreach (EnemyAIDebugFilter flag in EveryFlag)
            {
                Assert.IsTrue(seen.Add((int)flag), $"{flag} shares a bit with another flag");
            }
        }

        [Test]
        public void All_IsExactlyTheUnionOfEveryFlag()
        {
            EnemyAIDebugFilter union = EnemyAIDebugFilter.None;
            foreach (EnemyAIDebugFilter flag in EveryFlag) union |= flag;

            Assert.AreEqual(union, EnemyAIDebugFilter.All);
        }

        [Test]
        public void All_ContainsEveryFlag()
        {
            foreach (EnemyAIDebugFilter flag in EveryFlag)
            {
                Assert.IsTrue(EnemyAIDebugFilters.Has(EnemyAIDebugFilter.All, flag), $"All must include {flag}");
            }
        }

        [Test]
        public void Has_WithNone_IsFalse()
        {
            // (x & 0) != 0 is always false; asserted so nobody "optimises" it into something else.
            foreach (EnemyAIDebugFilter flag in EveryFlag)
            {
                Assert.IsFalse(EnemyAIDebugFilters.Has(EnemyAIDebugFilter.None, flag));
            }
        }

        // ── Formatting ───────────────────────────────────────────────────────

        [Test]
        public void Format_NoneAndAll_UseTheShortWords()
        {
            Assert.AreEqual("off", EnemyAIDebugFilters.Format(EnemyAIDebugFilter.None));
            Assert.AreEqual("all", EnemyAIDebugFilters.Format(EnemyAIDebugFilter.All));
        }

        [Test]
        public void Format_NamesEveryBitItIsGiven()
        {
            Assert.AreEqual("vision,los",
                EnemyAIDebugFilters.Format(EnemyAIDebugFilter.Vision | EnemyAIDebugFilter.LineOfSight));
            Assert.AreEqual("vision", EnemyAIDebugFilters.Format(EnemyAIDebugFilter.Vision));
        }

        [Test]
        public void Format_RoundTripsThroughTryParse()
        {
            // Whatever `ai` prints as the current state must be accepted back verbatim. This is the
            // property that makes copy-pasting the status line into the next command work.
            var samples = new List<EnemyAIDebugFilter>
            {
                EnemyAIDebugFilter.None,
                EnemyAIDebugFilter.All,
                EnemyAIDebugFilter.Vision,
                EnemyAIDebugFilter.Vision | EnemyAIDebugFilter.Hearing,
                EnemyAIDebugFilter.LineOfSight | EnemyAIDebugFilter.Target,
                EnemyAIDebugFilter.Vision | EnemyAIDebugFilter.Hearing | EnemyAIDebugFilter.CloseProximity
                    | EnemyAIDebugFilter.LineOfSight | EnemyAIDebugFilter.Target | EnemyAIDebugFilter.States,
            };

            // Every single-flag filter too, so a flag added without a Format entry cannot hide.
            foreach (EnemyAIDebugFilter flag in EveryFlag) samples.Add(flag);

            foreach (EnemyAIDebugFilter sample in samples)
            {
                string text = EnemyAIDebugFilters.Format(sample);

                Assert.IsTrue(EnemyAIDebugFilters.TryParse(text, out EnemyAIDebugFilter parsed, out string error),
                    $"'{text}' should parse back: {error}");
                Assert.AreEqual(sample, parsed, $"'{text}' must round-trip to {sample}");
            }
        }

        [Test]
        public void Format_IsOrderIndependent()
        {
            // The formatter walks its own catalogue order, so two masks that differ only in the order
            // the flags were set must print identically — otherwise a status line looks different for
            // the same state and reads as a change.
            EnemyAIDebugFilter a = EnemyAIDebugFilter.LineOfSight | EnemyAIDebugFilter.Vision;
            EnemyAIDebugFilter b = EnemyAIDebugFilter.Vision | EnemyAIDebugFilter.LineOfSight;

            Assert.AreEqual(EnemyAIDebugFilters.Format(a), EnemyAIDebugFilters.Format(b));
        }

        // ── Status text ──────────────────────────────────────────────────────

        [Test]
        public void DescribeAll_MarksExactlyTheEnabledFlags()
        {
            string text = EnemyAIDebugFilters.DescribeAll(
                EnemyAIDebugFilter.Vision | EnemyAIDebugFilter.LineOfSight);

            StringAssert.Contains("[x] vision", text);
            StringAssert.Contains("[x] los", text);
            StringAssert.Contains("[ ] hearing", text);
            StringAssert.Contains("[ ] states", text);
        }

        [Test]
        public void DescribeAll_WithNone_MarksNothingOn()
        {
            Assert.IsFalse(EnemyAIDebugFilters.DescribeAll(EnemyAIDebugFilter.None).Contains("[x]"));
        }

        [Test]
        public void DescribeAll_CoversEveryFlag()
        {
            string text = EnemyAIDebugFilters.DescribeAll(EnemyAIDebugFilter.None);

            foreach (EnemyAIDebugFilter flag in EveryFlag)
            {
                StringAssert.Contains(EnemyAIDebugFilters.Format(flag), text);
            }
        }

        [Test]
        public void Usage_ListsEveryFlagName()
        {
            string usage = EnemyAIDebugFilters.Usage;

            foreach (EnemyAIDebugFilter flag in EveryFlag)
            {
                StringAssert.Contains(EnemyAIDebugFilters.Format(flag), usage);
            }
        }

        // ── PermissiveIfEmpty ────────────────────────────────────────────────

        [Test]
        public void PermissiveIfEmpty_OnlyRewritesNone()
        {
            // Turning the overlay ON with a None filter would draw nothing, which reads as a broken
            // toggle rather than as an empty filter. The fallback must be All, and it must not touch
            // a filter that already says something.
            Assert.AreEqual(EnemyAIDebugFilter.All, EnemyAIDebugFilters.PermissiveIfEmpty(EnemyAIDebugFilter.None));

            foreach (EnemyAIDebugFilter flag in EveryFlag)
            {
                Assert.AreEqual(flag, EnemyAIDebugFilters.PermissiveIfEmpty(flag),
                    "a non-empty filter must be left exactly as it was");
            }

            Assert.AreEqual(EnemyAIDebugFilter.All,
                EnemyAIDebugFilters.PermissiveIfEmpty(EnemyAIDebugFilter.All));
        }
    }
}
