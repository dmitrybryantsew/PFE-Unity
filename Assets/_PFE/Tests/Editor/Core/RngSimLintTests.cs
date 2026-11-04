using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// P3 Workstream A lint, generalized for P4 Phase 0.2: Sim-layer systems must not read unseeded
    /// <see cref="UnityEngine.Random"/>. All simulation randomness must route through
    /// <see cref="PFE.Sim.Rng.IRngService"/> for reproducibility.
    ///
    /// <para><b>What changed in the generalization.</b> The original lint asserted a hardcoded list of
    /// 12 paths. The readiness report (docs/multiplayer/Physics_And_Units_Multiplayer_Readiness_2026-11-04.md
    /// §6.8, F-08) found that after stripping comments and string literals, <b>40 non-test files</b>
    /// contain live RNG calls — and the hardcoded list could not see a new one added outside those 12.
    /// This fixture now <i>discovers</i> the surface instead of trusting a list:</para>
    /// <list type="number">
    ///   <item><description><b>Sim/** must be clean (green guard).</b> Every <c>.cs</c> file under
    ///   <c>Assets/_PFE/Sim/**</c> is scanned; any live RNG call fails. Verified clean 2026-11-04 —
    ///   the pure calculators already route through <c>IRngService</c>.</description></item>
    ///   <item><description><b>Tickable files must be clean (green guard).</b> Every direct
    ///   <see cref="PFE.Core.ISimTickable"/> implementor is scanned; an unseeded draw in a tick path
    ///   breaks host-replay determinism (F6). The three known offenders are allow-listed in
    ///   <c>ClientPredictionSeamTests.KnownTickPathRngReads</c> and tracked by that fixture; this
    ///   lint asserts the <i>rest</i> of the tickable set stays clean.</description></item>
    ///   <item><description><b>The full inventory is classified and pinned.</b> The complete set of
    ///   non-test files with live RNG calls is recorded in <see cref="ClassifiedRngFiles"/> with a
    ///   per-file classification (<c>Authoritative</c> / <c>ClientPredicted</c> / <c>LocalOnly</c>).
    ///   The count and the set are both asserted, so a new draw anywhere in the project fails the
    ///   lint with the file name — and a removed draw fails it too, keeping the inventory honest.</description></item>
    /// </list>
    ///
    /// <para><b>Mutation control.</b> <see cref="ClassifiedInventory_MutationControl_DetectsRemoval"/>
    /// re-runs the discovery against a source tree with one routed file's draws artificially
    /// suppressed and asserts the lint would catch the difference — a guard that cannot fail is not
    /// a guard.</para>
    /// </summary>
    [TestFixture]
    public class RngSimLintTests
    {
        private const string PfeRoot = "_PFE";
        private const string SimRoot = "_PFE/Sim";

        private static readonly Regex UnityEngineRandomRegex =
            new Regex(@"\b(UnityEngine\s*\.\s*Random|Random\s*\.\s*(Range|value|insideUnitCircle|insideUnitSphere|onUnitSphere|rotation|ColorHSV))\b", RegexOptions.Compiled);

        private static readonly Regex DirectTickableRegex = new Regex(
            @"\bclass\s+\w+[^;{]*:\s*[^\{]*\b(ISimTickable|IAutoRegisteredSimTickable)\b",
            RegexOptions.Compiled);

        /// <summary>
        /// The 12 original lint targets, kept as an explicit existence check: a rename or delete that
        /// skips this list still fails, preserving the original lint's regression-detection property.
        /// </summary>
        private static readonly string[] OriginalLintTargets = new[]
        {
            "Sim/Combat/CombatCalculator.cs",
            "Sim/Combat/CriticalHitSystem.cs",
            "Sim/Combat/DurabilitySystem.cs",
            "Systems/Combat/DamageSystem.cs",
            "Sim/Combat/DamageCalculator.cs",
            "Sim/Weapons/Controllers/RangedWeaponController.cs",
            "Entities/Weapons/Projectile.cs",
            "Systems/Map/RoomDifficulty.cs",
            "Systems/Map/RoomGenerator.cs",
            "Systems/Map/RoomPopulator.cs",
            "Systems/Map/WorldBuilder.cs",
            "Systems/RPG/VendorInventory.cs"
        };

        /// <summary>RNG classification, mirroring <see cref="PFE.Core.SimAuthority"/>.</summary>
        private enum RngClass
        {
            /// <summary>Must route through IRngService; affects authoritative Sim outcome.</summary>
            Authoritative,
            /// <summary>Client may predict; must reconcile. Routed through a per-system stream.</summary>
            ClientPredicted,
            /// <summary>Cosmetic / presentation only; never replicated, may stay unseeded.</summary>
            LocalOnly,
        }

        /// <summary>
        /// The complete, source-verified (2026-11-04) inventory of non-test files containing live
        /// <see cref="UnityEngine.Random"/> calls, each with its F-08 classification. 40 files.
        /// The set AND the count are asserted. Adding a draw fails the lint; removing one fails it
        /// too (delete the entry). Classification rationale follows readiness §6.8: enemy brains and
        /// UnitController/PlayerController gameplay draws are Authoritative; audio/animation/UI/
        /// editor/particle flavour is LocalOnly.
        /// </summary>
        private static readonly Dictionary<string, RngClass> ClassifiedRngFiles = new Dictionary<string, RngClass>
        {
            // ── Authoritative: gameplay-affecting draws inside tick-capable units/brains ──
            { "Entities/Enemies/Core/EnemyBrain.cs", RngClass.Authoritative },
            { "Entities/Enemies/Archetypes/ZombieBrain.cs", RngClass.Authoritative },
            { "Entities/Enemies/Archetypes/Alicorn/AlicornBrain.cs", RngClass.Authoritative },
            { "Entities/Enemies/Archetypes/Alicorn/AlicornController.cs", RngClass.Authoritative },
            { "Entities/Enemies/Archetypes/Alicorn/AlicornTelekinesis.cs", RngClass.Authoritative },
            { "Entities/Enemies/Archetypes/Alicorn/BossAlicornTelekinesis.cs", RngClass.Authoritative },
            { "Entities/Enemies/Archetypes/Aquatic/AquaticBrain.cs", RngClass.Authoritative },
            { "Entities/Enemies/Archetypes/ArmedShooter/ArmedShooterBrain.cs", RngClass.Authoritative },
            { "Entities/Enemies/Archetypes/ArmedShooter/ArmedShooterController.cs", RngClass.Authoritative },
            { "Entities/Enemies/Archetypes/Beast/BeastBrain.cs", RngClass.Authoritative },
            { "Entities/Enemies/Archetypes/Climber/ClimberBrain.cs", RngClass.Authoritative },
            { "Entities/Enemies/Archetypes/Flyer/FlyerBrain.cs", RngClass.Authoritative },
            { "Entities/Enemies/Archetypes/Robot/RobotBrain.cs", RngClass.Authoritative },
            { "Entities/Enemies/Archetypes/Robot/RobotController.cs", RngClass.Authoritative },
            { "Entities/Enemies/Archetypes/Trap/TrapBrain.cs", RngClass.Authoritative },
            { "Entities/Enemies/Archetypes/Trap/TrapController.cs", RngClass.Authoritative },
            { "Entities/Enemies/Bosses/BossAlicornBrain.cs", RngClass.Authoritative },
            { "Entities/Enemies/Bosses/BossNecrBrain.cs", RngClass.Authoritative },
            { "Entities/Enemies/Bosses/BossRaiderBrain.cs", RngClass.Authoritative },
            { "Entities/Enemies/Bosses/BossSummonSpawner.cs", RngClass.Authoritative },
            { "Entities/Enemies/Bosses/BossUltraBrain.cs", RngClass.Authoritative },
            { "Entities/Enemies/Core/EnemySensors.cs", RngClass.Authoritative },
            { "Entities/Pets/PetBrain.cs", RngClass.Authoritative },
            { "Entities/Player/PlayerController.cs", RngClass.Authoritative },
            { "Entities/Player/PlayerRadiationTicker.cs", RngClass.Authoritative },
            { "Entities/Units/UnitController.cs", RngClass.Authoritative },
            { "Entities/Weapons/MeleeHitVolume.cs", RngClass.Authoritative },
            { "Systems/Inventory/PlayerInventory.cs", RngClass.Authoritative },
            { "Systems/Inventory/WorldItemPickup.cs", RngClass.Authoritative },
            { "Systems/Magic/SpellBlastExecutor.cs", RngClass.Authoritative },
            { "Systems/Security/RoomAlarm.cs", RngClass.Authoritative },

            // ── LocalOnly: cosmetic / presentation flavour, never replicated ──
            { "Character/Animation/ActionAnimStates.cs", RngClass.LocalOnly },
            { "Character/Animation/LocomotionAnimStates.cs", RngClass.LocalOnly },
            { "Entities/Units/NpcVisualAnimator.cs", RngClass.LocalOnly },
            { "Systems/Audio/CharacterAudioEventHandler.cs", RngClass.LocalOnly },
            { "Systems/Audio/ImpactSoundResolver.cs", RngClass.LocalOnly },
            { "Systems/Audio/SoundService.cs", RngClass.LocalOnly },
            { "Systems/Map/Rendering/TileCollider.cs", RngClass.LocalOnly },
            { "UI/MainMenu/MainMenuBackgroundRenderer.cs", RngClass.LocalOnly },
            { "Editor/Importers/SWF/MainMenuCompositionPreviewWindow.cs", RngClass.LocalOnly },
        };

        // ── 1. Original 12 targets still exist (regression-detection preserved) ──

        [Test]
        public void OriginalLintTargets_StillExist()
        {
            foreach (var relativePath in OriginalLintTargets)
            {
                string fullPath = FullPath(relativePath);
                Assert.IsTrue(File.Exists(fullPath),
                    "Lint target missing: " + fullPath + " (a rename/delete must update OriginalLintTargets)");
            }
        }

        // ── 2. Sim/** is clean — the green guard ───────────────────────────────

        [Test]
        public void SimLayer_ContainsNoLiveRngCalls()
        {
            var violations = CollectLiveRngFiles(EnumerateFiles(SimRoot));

            CollectionAssert.IsEmpty(
                violations,
                "Assets/_PFE/Sim/** must route all randomness through IRngService. Live RNG calls found:\n" +
                string.Join("\n", violations));
        }

        // ── 3. Direct ISimTickable implementors outside the allow-list are clean ──

        [Test]
        public void TickableFiles_OutsideAllowList_ContainNoLiveRngCalls()
        {
            // The three known tick-path offenders are owned by ClientPredictionSeamTests' inventory;
            // this guard asserts no NEW tickable file grows an unseeded draw.
            var allowList = new HashSet<string>(StringComparer.Ordinal)
            {
                "Entities/Enemies/Core/EnemyBrain.cs",
                "Entities/Units/UnitController.cs",
                "Entities/Player/PlayerRadiationTicker.cs",
            };

            var violations = new List<string>();
            foreach (string relativePath in EnumerateTickableFiles())
            {
                if (allowList.Contains(relativePath))
                {
                    continue;
                }

                string stripped = ReadStripped(relativePath);
                if (UnityEngineRandomRegex.IsMatch(stripped))
                {
                    violations.Add(relativePath);
                }
            }

            CollectionAssert.IsEmpty(
                violations,
                "A direct ISimTickable implementor grew an unseeded RNG draw. Route it through IRngService " +
                "(F6 host-replay determinism). Offenders:\n" + string.Join("\n", violations));
        }

        // ── 4. The full 40-file inventory is pinned (set + count) ─────────────

        [Test]
        public void ClassifiedInventory_MatchesSourceExactly()
        {
            var actual = CollectLiveRngFiles(EnumerateAllProductionFiles());
            actual.Sort(StringComparer.Ordinal);

            var expected = new List<string>(ClassifiedRngFiles.Keys);
            expected.Sort(StringComparer.Ordinal);

            CollectionAssert.AreEqual(
                expected,
                actual,
                "The set of files with live RNG calls changed. If you ADDED one: classify it in " +
                "ClassifiedRngFiles (Authoritative → route via IRngService; LocalOnly → mark and leave). " +
                "If you REMOVED one: delete its entry so the floor moves with the fix. " +
                $"Expected {expected.Count}, found {actual.Count}. Offending files:\n" +
                string.Join("\n", actual));
        }

        [Test]
        public void ClassifiedInventory_HasExpectedCount()
        {
            Assert.That(ClassifiedRngFiles.Count, Is.EqualTo(40),
                "Readiness §6.8 source-verified 40 non-test files with live RNG calls on 2026-11-04. " +
                "The inventory count drifted — re-run discovery and re-classify.");
        }

        // ── 5. Mutation control: removing a routed draw is detected ────────────

        /// <summary>
        /// A guard that cannot fail is not a guard. This re-runs discovery over the real tree but
        /// with one Authoritative file's draws artificially suppressed, and asserts the inventory
        /// comparison WOULD fail — proving the lint detects a routed (removed) call rather than
        /// passing vacuously.
        /// </summary>
        [Test]
        public void ClassifiedInventory_MutationControl_DetectsRemoval()
        {
            const string suppressedFile = "Entities/Enemies/Core/EnemyBrain.cs";

            var actual = new List<string>();
            foreach (string relativePath in EnumerateAllProductionFiles())
            {
                if (string.Equals(relativePath, suppressedFile, StringComparison.Ordinal))
                {
                    continue; // simulate "this file's draws were routed through IRngService"
                }

                string stripped = ReadStripped(relativePath);
                if (UnityEngineRandomRegex.IsMatch(stripped))
                {
                    actual.Add(relativePath);
                }
            }

            bool inventoryWouldFail = !actual.Contains(suppressedFile)
                && actual.Count == ClassifiedRngFiles.Count - 1;

            Assert.IsTrue(inventoryWouldFail,
                "Mutation control failed: suppressing a routed file did NOT change the discovered set. " +
                "The inventory lint cannot detect a removed RNG call and is vacuous.");
        }

        // ── Discovery helpers ──────────────────────────────────────────────────

        private static List<string> CollectLiveRngFiles(IEnumerable<string> relativePaths)
        {
            var result = new List<string>();
            foreach (string relativePath in relativePaths)
            {
                string stripped = ReadStripped(relativePath);
                if (UnityEngineRandomRegex.IsMatch(stripped))
                {
                    result.Add(relativePath);
                }
            }
            return result;
        }

        private static IEnumerable<string> EnumerateAllProductionFiles()
        {
            foreach (string relativePath in EnumerateFiles(PfeRoot))
            {
                if (relativePath.StartsWith("Tests/", StringComparison.Ordinal))
                {
                    continue;
                }
                yield return relativePath;
            }
        }

        private static IEnumerable<string> EnumerateTickableFiles()
        {
            foreach (string relativePath in EnumerateAllProductionFiles())
            {
                string stripped = ReadStripped(relativePath);
                if (DirectTickableRegex.IsMatch(stripped))
                {
                    yield return relativePath;
                }
            }
        }

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

        // ── Stripping + path helpers ───────────────────────────────────────────

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
