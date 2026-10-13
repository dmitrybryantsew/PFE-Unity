using System.Collections.Generic;
using NUnit.Framework;
using PFE.Systems.Campaign;

namespace PFE.Tests.EditMode.Systems.Campaign
{
    /// <summary>
    /// Pins <see cref="CampaignVariableCatalog"/> — the decision table behind the F2 "Campaign Vars"
    /// tab — so a row that loses its description, its oracle citation or its uniqueness fails here
    /// rather than silently printing a half-explained line in the shipped tab.
    ///
    /// <para><b>Why well-formedness is worth a fixture.</b> The tab draws every row's
    /// <c>Responsibility</c> and <c>Oracle</c> straight from this table, and the table's whole value is
    /// that the wording and the AS3 citation live in one place. These tests hold that place honest:
    /// unique ids (two "curLandId" rows would show one twice and hide the other), non-empty text (an
    /// empty responsibility renders a blank line under a trigger that then reads as "no description
    /// found"), and content pins for the entries the tab's own comments cite by name.</para>
    ///
    /// <para>Unity-free on purpose: the class it exercises is a plain static table, so this fixture runs
    /// through the offline wall like <see cref="CheckpointRulesTests"/> does.</para>
    /// </summary>
    [TestFixture]
    public sealed class CampaignVariableCatalogTests
    {
        [Test]
        public void EveryVariable_HasUniqueId_Summary_ResponsibilityAndOracle()
        {
            var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < CampaignVariableCatalog.Variables.Count; i++)
            {
                CampaignVariableInfo v = CampaignVariableCatalog.Variables[i];
                string where = $"Variables[{i}] (id='{v.Id}')";

                Assert.IsFalse(string.IsNullOrWhiteSpace(v.Id), $"{where}: id is empty.");
                Assert.IsTrue(seen.Add(v.Id), $"{where}: duplicate id.");
                Assert.IsFalse(string.IsNullOrWhiteSpace(v.Summary), $"{where}: summary is empty.");
                Assert.IsFalse(string.IsNullOrWhiteSpace(v.Responsibility), $"{where}: responsibility is empty.");
                Assert.IsFalse(string.IsNullOrWhiteSpace(v.Oracle), $"{where}: oracle citation is empty.");
            }
        }

        [Test]
        public void EveryImportantTrigger_HasUniqueName_ResponsibilityAndOracle()
        {
            var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < CampaignVariableCatalog.ImportantTriggers.Count; i++)
            {
                ImportantTriggerInfo t = CampaignVariableCatalog.ImportantTriggers[i];
                string where = $"ImportantTriggers[{i}] (name='{t.Name}')";

                Assert.IsFalse(string.IsNullOrWhiteSpace(t.Name), $"{where}: name is empty.");
                Assert.IsTrue(seen.Add(t.Name), $"{where}: duplicate name.");
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.Responsibility), $"{where}: responsibility is empty.");
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.Oracle), $"{where}: oracle citation is empty.");
            }
        }

        /// <summary>The tab's intro and section comments name these entries, and the guard-hoist rule
        /// for a no-player tab keys off the campaign living on CampaignManager — if a rename drops one
        /// of these rows, those comments are lying.</summary>
        [Test]
        public void Variables_CoverTheGameFieldSetTheTabCommentsName()
        {
            string[] required =
            {
                "curLandId", "missionId", "baseId", "triggers", "quests", "lands",
                "mReturn", "notes", "curCoord", "globalDif", "crea", "limits",
            };

            foreach (string id in required)
            {
                Assert.IsTrue(CampaignVariableCatalog.TryGetVariable(id, out _),
                    $"Variables is missing '{id}' — the F2 tab's comments cite it by name.");
            }
        }

        [Test]
        public void ImportantTriggers_CoverTheFlagsTheCampaignBranchesOn()
        {
            string[] required =
            {
                "firstroom", "noreturn", "nomed", "rbl_visited", "mbase_visited",
                "prob_<id>", "note_<id>", "frag_<id>", "mess_<name>",
                "fin", "storm", "pet_phoenix", "theend",
            };

            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (ImportantTriggerInfo t in CampaignVariableCatalog.ImportantTriggers)
            {
                seen.Add(t.Name);
            }

            foreach (string name in required)
            {
                Assert.IsTrue(seen.Contains(name),
                    $"ImportantTriggers is missing '{name}' — it is one of the flags the campaign branches on.");
            }
        }

        /// <summary>The tab draws [1]/[0]/[unset] buttons straight onto a row's name when the name is a
        /// single literal, and counts live matches instead when it is a family. This pins the rule the
        /// tab's <c>isLiteral</c> branch depends on: a literal name is a bare token it can set, and a
        /// non-literal is either a <c>&lt;id&gt;</c> prefix or a '/'-separated list of names.</summary>
        [Test]
        public void ImportantTriggers_LiteralNamesAreBareTokens_NonLiteralsAreCountable()
        {
            foreach (ImportantTriggerInfo t in CampaignVariableCatalog.ImportantTriggers)
            {
                bool isLiteral = t.Name.IndexOf('<') < 0 && t.Name.IndexOf('/') < 0;

                if (isLiteral)
                {
                    Assert.IsFalse(t.Name.Contains(" "),
                        $"'{t.Name}' is drawn as a literal trigger with quick-set buttons but is not a " +
                        "bare token — no trigger can ever be called that.");
                    continue;
                }

                if (t.Name.IndexOf('<') >= 0)
                {
                    Assert.IsFalse(t.Name.Contains("/"),
                        $"'{t.Name}' mixes a <id> placeholder with a '/' list — the tab counts it as one " +
                        "or the other and cannot count both.");
                    continue;
                }

                string[] parts = t.Name.Split('/');
                foreach (string raw in parts)
                {
                    string part = raw.Trim();
                    Assert.IsFalse(string.IsNullOrEmpty(part), $"'{t.Name}' has an empty '/'-separated name.");
                    Assert.IsFalse(part.Contains("<") || part.Contains(">"),
                        $"'{t.Name}' mixes literal names with a placeholder.");
                }
            }
        }

        /// <summary>Availability is the flag the tab colour-codes and the "not ported" section filters
        /// on; an unset availability would silently default to 0 (Live) and claim a knob that does not
        /// exist.</summary>
        [Test]
        public void Variables_HaveAnExplicitAvailability()
        {
            for (int i = 0; i < CampaignVariableCatalog.Variables.Count; i++)
            {
                CampaignVariableInfo v = CampaignVariableCatalog.Variables[i];
                Assert.IsTrue(
                    v.Availability == CampaignVariableAvailability.Live ||
                    v.Availability == CampaignVariableAvailability.Derived ||
                    v.Availability == CampaignVariableAvailability.NotPorted,
                    $"Variables[{i}] ('{v.Id}') has no availability.");
            }

            // The tab's guard-and-sections logic assumes at least one of each: a Live set to draw in
            // section A, and a NotPorted set for section E.
            int live = 0, notPorted = 0;
            foreach (CampaignVariableInfo v in CampaignVariableCatalog.Variables)
            {
                if (v.Availability == CampaignVariableAvailability.Live) live++;
                if (v.Availability == CampaignVariableAvailability.NotPorted) notPorted++;
            }

            Assert.Greater(live, 0, "no Live variables — section A of the tab would be empty.");
            Assert.Greater(notPorted, 0, "no NotPorted variables — section E of the tab would be empty.");
        }
    }
}
