using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// P3 Workstream A lint: Sim-layer systems must not read unseeded UnityEngine.Random.
    /// All simulation randomness must route through IRngService for reproducibility.
    /// </summary>
    [TestFixture]
    public class RngSimLintTests
    {
        private static readonly Regex UnityEngineRandomRegex =
            new Regex(@"\b(UnityEngine\s*\.\s*Random|Random\s*\.\s*(Range|value|insideUnitCircle|insideUnitSphere|onUnitSphere|rotation|ColorHSV))\b", RegexOptions.Compiled);

        private static readonly string[] SimFilesToCheck = new[]
        {
            "Systems/Combat/CombatCalculator.cs",
            "Systems/Combat/CriticalHitSystem.cs",
            "Systems/Combat/DurabilitySystem.cs",
            "Systems/Weapons/DamageResolver.cs",
            "Systems/Weapons/Controllers/RangedWeaponController.cs",
            "Entities/Weapons/Projectile.cs",
            "Systems/Map/RoomDifficulty.cs",
            "Systems/Map/RoomGenerator.cs",
            "Systems/Map/RoomPopulator.cs",
            "Systems/Map/WorldBuilder.cs",
            "Systems/RPG/VendorInventory.cs"
        };

        [Test]
        public void SimSystems_DoNotUseUnityEngineRandom()
        {
            var violations = new List<string>();

            foreach (var relativePath in SimFilesToCheck)
            {
                string stripped = ReadStripped(relativePath);
                foreach (Match match in UnityEngineRandomRegex.Matches(stripped))
                {
                    violations.Add($"{relativePath}: line {LineOf(stripped, match.Index)} ({match.Value})");
                }
            }

            CollectionAssert.IsEmpty(
                violations,
                "Sim files must use IRngService rather than UnityEngine.Random for determinism. Violations found:\n" +
                string.Join("\n", violations));
        }

        private static string ReadStripped(string relativePath)
        {
            string fullPath = Path.Combine(
                Application.dataPath,
                "_PFE",
                relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.IsTrue(
                File.Exists(fullPath),
                "Lint target missing: " + fullPath);

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
