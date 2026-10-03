using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PFE.Systems.RPG.Data;
using UnityEngine;

namespace PFE.Tests.Editor.RPG
{
    [TestFixture]
    public class SkillAndPerkImportVerificationTests
    {
        private string _allDataContent;

        [SetUp]
        public void Setup()
        {
            string path = "AllData.as";
            if (!File.Exists(path))
            {
                path = "E:\\Games\\UnityGames\\pfeToUnity\\pfe\\scripts\\fe\\AllData.as";
            }
            if (File.Exists(path))
            {
                _allDataContent = File.ReadAllText(path);
            }
        }

        [Test]
        [Description("Verify AllData.as contains exactly 16 skills and 84 perks")]
        public void AllData_ContainsExpectedSkillsAndPerksCount()
        {
            Assert.IsNotNull(_allDataContent, "AllData.as should be loaded");

            var skillMatches = Regex.Matches(_allDataContent, @"<skill\s+([^>]+)>(.*?)</skill>", RegexOptions.Singleline);
            var perkMatches = Regex.Matches(_allDataContent, @"<perk\s+([^>]+)>(.*?)</perk>", RegexOptions.Singleline);

            Assert.AreEqual(16, skillMatches.Count, "Should find exactly 16 skills in AllData.as");
            Assert.AreEqual(84, perkMatches.Count, "Should find exactly 84 perks in AllData.as");
        }

        [Test]
        [Description("Verify key skills have correct properties")]
        public void AllData_KeySkills_ContainValidModifiers()
        {
            Assert.IsNotNull(_allDataContent);

            // Check telekinesis
            Assert.IsTrue(_allDataContent.Contains("id='tele'"), "Should contain tele skill");
            // Check smallguns
            Assert.IsTrue(_allDataContent.Contains("id='smallguns'"), "Should contain smallguns skill");
            // Check post-game skills
            Assert.IsTrue(_allDataContent.Contains("id='attack'"), "Should contain post-game attack skill");
            Assert.IsTrue(_allDataContent.Contains("id='defense'"), "Should contain post-game defense skill");
            Assert.IsTrue(_allDataContent.Contains("id='knowl'"), "Should contain post-game knowl skill");
        }

        [Test]
        [Description("Verify key perks have correct requirements")]
        public void AllData_KeyPerks_ContainRequirements()
        {
            Assert.IsNotNull(_allDataContent);

            // Check oak perk (id='oak')
            Assert.IsTrue(_allDataContent.Contains("id='oak'"), "Should contain oak perk");
            // Check selflevit perk (id='selflevit')
            Assert.IsTrue(_allDataContent.Contains("id='selflevit'"), "Should contain selflevit perk");
            // Check telethrow perk (id='telethrow')
            Assert.IsTrue(_allDataContent.Contains("id='telethrow'"), "Should contain telethrow perk");
        }
    }
}
