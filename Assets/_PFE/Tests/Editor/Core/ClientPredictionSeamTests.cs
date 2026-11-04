using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PFE.Core;
using UnityEngine;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// P4 Phase 0.1 — the F4 guard. F4 (docs/Roadmap/05 §1.4) requires that a client can run its
    /// own <see cref="SimLoop"/> for the local entity only, <b>without the host</b>. The readiness
    /// report (docs/multiplayer/Physics_And_Units_Multiplayer_Readiness_2026-11-04.md §6.9, F-09)
    /// warns that under the current architecture this proof fails, because Sim-facing code still
    /// reaches host-only globals.
    ///
    /// <para><b>What this fixture pins, and why each half exists.</b></para>
    /// <list type="number">
    ///   <item><description><b>Sim purity (source lint, must stay green).</b> No file under
    ///   <c>Assets/_PFE/Sim/**</c> may read a host-only global (<c>UnityEngine.Random</c>,
    ///   <c>FindFirstObjectByType</c>, <c>Camera.main</c>, <c>Input.*</c>) or write a
    ///   <c>Transform</c>. The pure rule layer must be identical on host and client (05 §1.5 rule
    ///   3). Verified clean 2026-11-04; this guard keeps it that way.</description></item>
    ///   <item><description><b>Tick-path RNG inventory (explicit, mutation-controlled).</b> The set
    ///   of <i>direct</i> <see cref="ISimTickable"/> implementors whose <see cref="ISimTickable.SimTick"/>
    ///   body reads an unseeded <c>UnityEngine.Random</c> is recorded below as an exact allow-list.
    ///   These are the deterministic-replay blockers (readiness F-08 / F6): a host replay cannot be
    ///   deterministic while a tick draws from an unseeded source. A new draw in a tick fails the
    ///   guard; removing one fails it too (the list is a floor), so it cannot silently rot.</description></item>
    ///   <item><description><b>Runtime probe.</b> A bare <see cref="SimLoop"/> must step 300 ticks
    ///   with only a scripted recorder registered — pinning <see cref="SimLoop.StepOnce"/> as the
    ///   host-free network stepping seam the readiness report (§3.1) says it already is.</description></item>
    /// </list>
    ///
    /// <para><b>Scope is deliberately narrow and defensible.</b> The inventory counts only globals
    /// read <i>inside a SimTick body</i> of a <i>direct</i> implementor — not <c>Time.*</c> in a
    /// view-path <c>FixedUpdate</c> (a separate, cosmetic concern), and not brain subclasses that
    /// inherit their tick from <c>EnemyBrain</c> (the base's draw is the single source). The wider
    /// <c>FindFirstObjectByType</c> / <c>Camera.main</c> / <c>Input.*</c> coupling in the
    /// <i>presentation</i> layer is real but is Phase 2.2's <c>IPlayerSlotContext</c> work, tracked
    /// by the readiness report's F-09; this fixture's job is the narrower, falsifiable F4 seed.</para>
    ///
    /// <para><b>Why a source lint and not only a runtime test.</b> A MonoBehaviour defect escapes
    /// every offline harness (readiness §5 consequence 4, §11 item 6): the arithmetic can be perfect
    /// and the <i>wiring</i> wrong. Only a source lint can assert "the tick did not secretly read an
    /// unseeded global".</para>
    /// </summary>
    [TestFixture]
    public class ClientPredictionSeamTests
    {
        private const string PfeRoot = "_PFE";
        private const string SimRoot = "_PFE/Sim";

        /// <summary>Host-only globals an F4 prediction / replay path must never read.</summary>
        private static readonly Regex HostOnlyGlobalRegex = new Regex(
            @"\b(" +
            @"UnityEngine\s*\.\s*Random" +
            @"|Random\s*\.\s*(Range|value|insideUnitCircle|insideUnitSphere|onUnitSphere|rotation|ColorHSV)" +
            @"|FindFirstObjectByType|FindObjectsByType|FindAnyObjectByType" +
            @"|Camera\s*\.\s*main" +
            @"|Input\s*\.\s*(GetKey|GetKeyDown|GetKeyUp|GetMouse|GetMouseButton|GetMouseButtonDown|GetMouseButtonUp|mousePosition|GetAxis|GetAxisRaw|GetButton|anyKey)" +
            @")\b",
            RegexOptions.Compiled);

        /// <summary>Unseeded RNG specifically — the determinism blocker the inventory tracks.</summary>
        private static readonly Regex UnseededRngRegex = new Regex(
            @"\b(UnityEngine\s*\.\s*Random|Random\s*\.\s*(Range|value|insideUnitCircle|insideUnitSphere|onUnitSphere|rotation|ColorHSV))\b",
            RegexOptions.Compiled);

        /// <summary>A Transform write is the view owning Sim truth — readiness §5's exact defect.</summary>
        private static readonly Regex TransformWriteRegex = new Regex(
            @"\btransform\s*\.\s*(position|rotation|localPosition|localRotation|localScale)\s*=",
            RegexOptions.Compiled);

        /// <summary>Matches a class that directly declares ISimTickable (or the always-on marker).</summary>
        private static readonly Regex DirectTickableRegex = new Regex(
            @"\bclass\s+\w+[^;{]*:\s*[^\{]*\b(ISimTickable|IAutoRegisteredSimTickable)\b",
            RegexOptions.Compiled);

        // ── 1. Sim purity — the guard that must stay green ─────────────────────

        [Test]
        public void SimLayer_ReadsNoHostOnlyGlobal_AndWritesNoTransform()
        {
            var violations = new List<string>();

            foreach (string relativePath in EnumerateFiles(SimRoot))
            {
                string stripped = ReadStripped(relativePath);

                foreach (Match match in HostOnlyGlobalRegex.Matches(stripped))
                {
                    violations.Add($"{relativePath}:{LineOf(stripped, match.Index)} host-only global `{match.Value.Trim()}`");
                }

                foreach (Match match in TransformWriteRegex.Matches(stripped))
                {
                    violations.Add($"{relativePath}:{LineOf(stripped, match.Index)} Transform write `{match.Value.Trim()}`");
                }
            }

            CollectionAssert.IsEmpty(
                violations,
                "Assets/_PFE/Sim/** must stay free of host-only globals and Transform writes " +
                "(docs/Roadmap/05 §1.5 rules 1–3; readiness §11 item 6). Violations:\n" +
                string.Join("\n", violations));
        }

        // ── 2. Tickable RNG inventory — the deterministic-replay blockers ──────

        /// <summary>
        /// The exact set of direct <see cref="ISimTickable"/> implementors whose file reads unseeded
        /// <c>UnityEngine.Random</c>, source-verified 2026-11-04. These are the F6 / F-08
        /// deterministic-replay blockers: a tickable file is the unit that must be replay-clean, and
        /// an unseeded draw anywhere in it (whether reached directly from <c>SimTick</c> or through a
        /// helper it calls, e.g. <c>PlayerRadiationTicker.SimTick → Step() → Random.value</c>) breaks
        /// host-replay determinism. Each entry is <c>relative path = reason</c>. Routing every draw in
        /// a file through <c>IRngService</c> removes the entry — delete it here so the floor moves
        /// with the fix.
        /// </summary>
        private static readonly Dictionary<string, string> KnownTickPathRngReads = new Dictionary<string, string>
        {
            // EnemyBrain drives every archetype/boss brain's tick; its unseeded draws (target fidgets,
            // bark rolls) are inherited by all of them. One base-class entry covers the tree.
            { "Entities/Enemies/Core/EnemyBrain.cs", "unseeded RNG in EnemyBrain (tick base for every archetype/boss brain); F-08" },
            // UnitController: hit-avoid flavour, bark rolls, dissolve emission.
            { "Entities/Units/UnitController.cs", "unseeded RNG in UnitController (F-08)" },
            // PlayerRadiationTicker: the geiger-click gate draws Random.value from Step().
            { "Entities/Player/PlayerRadiationTicker.cs", "unseeded RNG in PlayerRadiationTicker (geiger click gate)" },
        };

        [Test]
        public void TickPathRngInventory_MatchesSourceExactly()
        {
            var actual = new List<string>();

            foreach (string relativePath in EnumerateTickableFiles())
            {
                string stripped = ReadStripped(relativePath);
                if (UnseededRngRegex.IsMatch(stripped))
                {
                    actual.Add(relativePath);
                }
            }

            actual.Sort(StringComparer.Ordinal);
            var expected = new List<string>(KnownTickPathRngReads.Keys);
            expected.Sort(StringComparer.Ordinal);

            CollectionAssert.AreEqual(
                expected,
                actual,
                "The set of SimTick bodies reading unseeded RNG changed. If you ADDED one: that is a " +
                "new deterministic-replay blocker — route it through IRngService. If you REMOVED one: " +
                "delete its entry from KnownTickPathRngReads so the floor moves with the fix. " +
                "Offending files:\n" + string.Join("\n", actual));
        }

        /// <summary>
        /// Mutation control for the inventory: the allow-list must be non-empty (a guard that can
        /// never fail is not a guard), and every entry must still exist on disk. Routing one draw
        /// through IRngService without updating the list fails
        /// <see cref="TickPathRngInventory_MatchesSourceExactly"/>; this test fails if the list
        /// itself is hollowed out instead. When the last draw is routed, replace this guard with the
        /// positive assertion that no SimTick body reads unseeded RNG.
        /// </summary>
        [Test]
        public void TickPathRngInventory_IsNonEmpty_AndEntriesExist()
        {
            Assert.That(KnownTickPathRngReads.Count, Is.GreaterThan(0),
                "The tick-path RNG inventory is empty. F-08 has landed — replace this guard with " +
                "the positive assertion that NO SimTick body reads unseeded RNG.");

            foreach (string relativePath in KnownTickPathRngReads.Keys)
            {
                string fullPath = FullPath(relativePath);
                Assert.IsTrue(File.Exists(fullPath),
                    $"Inventory entry has no file on disk: {fullPath}. Keep the inventory in sync with renames/deletes.");
            }
        }

        // ── 3. Runtime probe — the loop itself is host-free ────────────────────

        /// <summary>
        /// The one runtime half of F4 that IS offline-checkable today: a bare SimLoop must step 300
        /// ticks with only a scripted-recorder tickable registered, without throwing and advancing
        /// monotonically from 0. This pins <see cref="SimLoop.StepOnce"/> as the network stepping
        /// seam. The predicted-entity half (motor + locomotion) is covered by the GoldenTraceHarness
        /// fixtures; the global-read half is the source lint above.
        /// </summary>
        [Test]
        public void SimLoop_StepsThreeHundredTicks_WithoutHostOnlyGlobal()
        {
            var clock = new SimClock(SimClock.CanonicalTicksPerSecond);
            var loop = new SimLoop(clock, null);
            var recorder = new IndexRecorder();

            loop.Register(recorder);

            Assert.DoesNotThrow(() =>
            {
                for (int i = 0; i < 300; i++)
                {
                    loop.StepOnce();
                }
            }, "SimLoop.StepOnce must run 300 ticks headless with no host-only dependency.");

            Assert.That(recorder.Count, Is.EqualTo(300), "every StepOnce dispatched exactly one tick");
            Assert.That(loop.TickIndex, Is.EqualTo(299), "tick index is monotonic from 0");
            Assert.That(recorder.FirstIndex, Is.EqualTo(0));
            Assert.That(recorder.LastIndex, Is.EqualTo(299));
        }

        private sealed class IndexRecorder : ISimTickable
        {
            public int Count;
            public int FirstIndex = -1;
            public int LastIndex = -1;
            public int TickOrder => SimTickOrder.Input;

            public void SimTick(int tickIndex)
            {
                if (FirstIndex < 0) FirstIndex = tickIndex;
                LastIndex = tickIndex;
                Count++;
            }
        }

        // ── File enumeration ───────────────────────────────────────────────────

        /// <summary>All .cs files under a root, as forward-slash paths relative to Assets/_PFE.</summary>
        private static IEnumerable<string> EnumerateFiles(string rootRelativeToPfe)
        {
            string fullRoot = Path.Combine(Application.dataPath, rootRelativeToPfe.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(fullRoot))
            {
                yield break;
            }

            string pfePrefix = Path.Combine(Application.dataPath, PfeRoot) + Path.DirectorySeparatorChar;

            foreach (string file in Directory.EnumerateFiles(fullRoot, "*.cs", SearchOption.AllDirectories))
            {
                string relative = file.Substring(pfePrefix.Length);
                yield return relative.Replace(Path.DirectorySeparatorChar, '/');
            }
        }

        /// <summary>
        /// All non-test .cs files under Assets/_PFE that directly declare an ISimTickable
        /// implementation. Subclasses that inherit their tick (archetype brains via EnemyBrain) are
        /// represented by their base's entry, so they are not double-counted here.
        /// </summary>
        private static IEnumerable<string> EnumerateTickableFiles()
        {
            foreach (string relativePath in EnumerateFiles(PfeRoot))
            {
                if (relativePath.StartsWith("Tests/", StringComparison.Ordinal))
                {
                    continue;
                }

                string stripped = ReadStripped(relativePath);
                if (DirectTickableRegex.IsMatch(stripped))
                {
                    yield return relativePath;
                }
            }
        }

        // ── Stripping + path helpers (same recipe as RngSimLintTests) ──────────

        private static string FullPath(string relativePath)
        {
            return Path.Combine(
                Application.dataPath,
                PfeRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        private static string ReadStripped(string relativePath)
        {
            string fullPath = FullPath(relativePath);
            Assert.IsTrue(File.Exists(fullPath), "Lint target missing: " + fullPath);
            return StripCommentsAndStrings(File.ReadAllText(fullPath));
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

        private static string StripCommentsAndStrings(string source)
        {
            var sb = new StringBuilder(source.Length);
            int i = 0;
            while (i < source.Length)
            {
                if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
                {
                    while (i < source.Length && source[i] != '\n') { sb.Append(' '); i++; }
                }
                else if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
                {
                    sb.Append("  ");
                    i += 2;
                    while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                    {
                        sb.Append(source[i] == '\n' ? '\n' : ' ');
                        i++;
                    }
                    if (i < source.Length) { sb.Append(' '); i++; }
                    if (i < source.Length) { sb.Append(' '); i++; }
                }
                else if (source[i] == '"')
                {
                    sb.Append(' ');
                    i++;
                    while (i < source.Length && source[i] != '"')
                    {
                        if (source[i] == '\\' && i + 1 < source.Length) { sb.Append("  "); i += 2; continue; }
                        sb.Append(source[i] == '\n' ? '\n' : ' ');
                        i++;
                    }
                    if (i < source.Length) { sb.Append(' '); i++; }
                }
                else
                {
                    sb.Append(source[i]);
                    i++;
                }
            }
            return sb.ToString();
        }
    }
}
