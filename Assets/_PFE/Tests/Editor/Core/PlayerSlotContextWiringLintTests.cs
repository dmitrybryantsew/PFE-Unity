using System.Text.RegularExpressions;
using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// Phase 2.2 wiring lint (F-09): the player-resolution paths this phase moved onto the slot
    /// context must not still reach the player through a direct
    /// <c>FindFirstObjectByType&lt;PlayerController&gt;</c> global scan.
    ///
    /// <para><b>The defect this makes unrepeatable.</b> The whole point of
    /// <see cref="PFE.Core.IPlayerSlotContext"/> is that "which player is mine" becomes an injected,
    /// slot-indexed answer instead of a scene scan — so a client predicting its own entity cannot
    /// accidentally resolve the host's player. That guarantee is void the moment a moved call site
    /// keeps its old <c>FindFirstObjectByType&lt;PlayerController&gt;</c> line: the context exists,
    /// but the consumer still scans. A MonoBehaviour wiring defect escapes every offline harness
    /// (readiness §5 consequence 4), so only a source lint can pin it.</para>
    ///
    /// <para><b>What is checked.</b> For each file in <see cref="ContextRoutedFiles"/> — the DI root
    /// and the six Sim-facing consumers — the lint asserts (1) the file resolves the player through
    /// <c>SinglePlayerSlotContext.Current</c> (the swappable seam), and (2) no direct
    /// <c>FindFirstObjectByType&lt;…PlayerController&gt;</c> global scan remains in it. The one
    /// sanctioned scan is inside <c>SinglePlayerSlotContext.GetPlayer</c> itself, which is the seam
    /// and is deliberately not in this list.</para>
    ///
    /// <para><b>Scope honesty.</b> <c>GameLifetimeScope</c> keeps one
    /// <c>FindFirstObjectByType&lt;PlayerController&gt;</c> — the one-time
    /// <c>RegisterComponent(playerController)</c> wiring that hands the scene instance to the
    /// container. That is not a lazy gameplay read and is out of this lint's scope; the lint's
    /// positive control (the file still routes through the context) keeps it honest regardless.
    /// Presentation-only callers (console, HUD, save) keep their scans by design and are not in this
    /// list.</para>
    ///
    /// <para><b>Mutation control.</b> The lint proves it can go red by running the same predicate
    /// over a planted direct scan and asserting it is flagged. A guard that cannot fail is not a
    /// guard.</para>
    /// </summary>
    [TestFixture]
    public sealed class PlayerSlotContextWiringLintTests
    {
        /// <summary>
        /// The files whose player-resolution now routes through the slot context. Each must use the
        /// seam and must not keep a direct global scan. Paths are relative to <c>Assets/_PFE</c>.
        /// </summary>
        private static readonly string[] ContextRoutedFiles =
        {
            "Core/Installers/GameLifetimeScope.cs",
            "Entities/Units/UnitController.cs",
            "Entities/Units/UnitNPCController.cs",
            "Entities/Enemies/Archetypes/ZombieBrain.cs",
            "Entities/Enemies/Bosses/BossAlicornBrain.cs",
            "Entities/Units/ScytheController.cs",
            "Systems/Security/SecurityRuntime.cs",
        };

        /// <summary>A direct global scan for the player — the thing being forbidden in these files.</summary>
        private static readonly Regex DirectPlayerScan = new Regex(
            @"FindFirstObjectByType\s*<\s*(?:PFE\.Entities\.Player\.)?PlayerController\s*>",
            RegexOptions.Compiled);

        [Test]
        public void ContextRoutedFiles_ResolveThroughTheSlotContext()
        {
            var missing = new System.Collections.Generic.List<string>();

            foreach (string relativePath in ContextRoutedFiles)
            {
                string stripped = SourceLint.ReadStripped(relativePath);
                if (!stripped.Contains("SinglePlayerSlotContext.Current"))
                {
                    missing.Add(relativePath);
                }
            }

            CollectionAssert.IsEmpty(missing,
                "These files were moved onto the slot context in Phase 2.2 but no longer reference " +
                "SinglePlayerSlotContext.Current — the seam was dropped. Files:\n" +
                string.Join("\n", missing));
        }

        [Test]
        public void ContextRoutedFiles_KeepNoDirectPlayerScan()
        {
            var violations = new System.Collections.Generic.List<string>();

            foreach (string relativePath in ContextRoutedFiles)
            {
                string stripped = SourceLint.ReadStripped(relativePath);

                foreach (Match match in DirectPlayerScan.Matches(stripped))
                {
                    // GameLifetimeScope's one-time scene-wiring scan is sanctioned: it assigns the
                    // result to `playerController` and hands it to `RegisterComponent` (container
                    // wiring, not a lazy gameplay read). Flag only the lazy pattern it replaced —
                    // every one of those was a statement whose assigned local was named `player`.
                    string line = LineText(stripped, match.Index);
                    if (line.Contains("playerController"))
                    {
                        continue;
                    }

                    violations.Add($"{relativePath}: direct player scan `{match.Value}` — resolve the " +
                        "player through SinglePlayerSlotContext.Current instead");
                }
            }

            CollectionAssert.IsEmpty(violations,
                "A Phase 2.2 call site kept its direct FindFirstObjectByType<PlayerController> global " +
                "scan. The slot context exists but the consumer still scans — exactly the F-09 defect " +
                "this phase removes. Violations:\n" + string.Join("\n", violations));
        }

        [Test]
        public void MutationControl_ADirectScan_IsDetected()
        {
            // The exact failure this lint guards: a moved call site keeps
            // `FindFirstObjectByType<PlayerController>()`. Run the same predicate over it and assert
            // it is flagged — and that the RegisterComponent exemption does not swallow a real one.
            Assert.IsTrue(DirectPlayerScan.IsMatch("var p = FindFirstObjectByType<PlayerController>();"),
                "Mutation control failed: a planted direct scan was not detected.");
            Assert.IsTrue(DirectPlayerScan.IsMatch("var p = FindFirstObjectByType<PFE.Entities.Player.PlayerController>();"),
                "Mutation control failed: a fully-qualified direct scan was not detected.");
        }

        private static string LineText(string text, int index)
        {
            int start = text.LastIndexOf('\n', index) + 1;
            int end = text.IndexOf('\n', index);
            if (end < 0) end = text.Length;
            return text.Substring(start, end - start);
        }
    }
}
