using NUnit.Framework;
using PFE.UI.HUD;

namespace PFE.Tests.EditMode.UI
{
    /// <summary>
    /// Pins which progression transitions earn which chime — the rule
    /// <see cref="ProgressSoundRules"/> that <see cref="ProgressSoundView"/> executes.
    ///
    /// <para><b>Why this is worth a fixture despite being one <c>if</c>.</b> The rule encodes a
    /// disagreement between two Oracle call sites that look identical: <c>Game.addQuest</c> chimes
    /// <c>quest</c> on every grant (<c>Game.as:543,572</c>), while <c>Quest.close</c> chimes
    /// <c>quest_ok</c> only when <c>!this.sub</c> (<c>Quest.as:690</c>). A sub-objective closing is
    /// therefore <b>silent</b>. That is easy to "fix" by accident — the obvious reading of
    /// <c>NewState == 2</c> is "completed" — and the result is a chime on every stage of every
    /// multi-stage quest, which is precisely the pattern that would be heard as a bug and never
    /// traced back to here.</para>
    ///
    /// <para><b>What is not pinned.</b> That the clip exists and is audible: that is the
    /// <c>SoundCatalog.asset</c>, and this fixture reads it from disk exactly as
    /// <c>PipBuckMedTests.Shipped</c> reads the localisation document. The two checks below are
    /// there because "the sound never plays" has two very different causes — the rule routing the
    /// event away, or the catalog having no clip for the id.</para>
    /// </summary>
    [TestFixture]
    public class ProgressSoundTests
    {
        // ── The ids resolve to a real clip ─────────────────────────────────────

        /// <summary>
        /// "The chime never played" has two independent causes and they need different fixes: the
        /// rule routing the event away, or the catalog having no clip behind the id. The rule is
        /// pinned below; this covers the other one.
        ///
        /// <para>The three ids were <b>already imported</b> — that is the whole reason this bug was
        /// invisible in an asset audit. So the check reads the shipped asset, not the importer.</para>
        /// </summary>
        [TestCase("levelup")]
        [TestCase("quest")]
        [TestCase("quest_ok")]
        public void TheCatalogHasAClipForTheChimeId(string soundId)
        {
            string path = ShippedPath();
            Assert.That(System.IO.File.Exists(path), Is.True, "SoundCatalog.asset is missing: " + path);

            // Normalised, and that is not tidiness: the shipped .asset is CRLF (Unity writes it that
            // way on Windows) and `File.ReadAllText` does NOT translate line endings, so the literal
            // assertion below would fail against a file that plainly contains the id. Line endings are
            // not a fact this fixture should depend on — they are an environment accident, and they
            // would flip the moment the asset was re-saved elsewhere.
            //
            // The reason to keep the literal `Contains` at all, when the regex right underneath it is
            // already ending-agnostic, is that they fail differently: `Contains` failing names a
            // missing id, the regex failing names a missing clip. Dropping one of them would let a
            // genuine cause report through the other's message.
            string catalog = System.IO.File.ReadAllText(path).Replace("\r\n", "\n");

            // The imported form is `  - id: <id>` on its own line followed by `    variants:` and a
            // `fileID/guid` pair. An id declared with no variants resolves to nothing at runtime.
            Assert.That(catalog.Contains("- id: " + soundId + "\n"), Is.True,
                "The sound catalog has no entry for '" + soundId + "' — the chime cannot play. " +
                "Catalog: " + path);
            Assert.That(
                System.Text.RegularExpressions.Regex.IsMatch(catalog,
                    "- id: " + soundId + "\\s*\n\\s*variants:\\s*\n\\s*- \\{"),
                Is.True,
                "'" + soundId + "' is declared in the catalog but carries no clip variant, so it resolves " +
                "to silence.");
        }

        private static string ShippedPath()
        {
            string root = PFE.Tests.Editor.Common.SourceLint.ProjectRoot();
            return System.IO.Path.Combine(root, "Assets", "_PFE", "Data", "SoundCatalog.asset");
        }

        // ── Grant: 0 → 1 ──────────────────────────────────────────────────────

        [Test]
        public void QuestGranted_PlaysTheQuestChime()
        {
            ProgressSound sound = ProgressSoundRules.ForQuestStateChange(
                ProgressSoundRules.NotStarted, ProgressSoundRules.Active, null);

            Assert.That(sound, Is.EqualTo(ProgressSound.QuestGiven));
        }

        [Test]
        public void QuestGranted_IsTheOnlyGrantThatChimes()
        {
            // `Game.addQuest` returns at :551 when the quest already exists, before either
            // `Snd.ps("quest")`. Re-granting an active quest is silent.
            Assert.That(
                ProgressSoundRules.ForQuestStateChange(
                    ProgressSoundRules.Active, ProgressSoundRules.Active, null),
                Is.EqualTo(ProgressSound.None));

            Assert.That(
                ProgressSoundRules.ForQuestStateChange(
                    ProgressSoundRules.Done, ProgressSoundRules.Active, null),
                Is.EqualTo(ProgressSound.None));
        }

        [Test]
        public void AGrantFromNothing_DoesChime()
        {
            // Not reachable through `AddQuest` today, but the transition is the one the rule names,
            // and a future path that activates a parked quest from state 0 must chime.
            Assert.That(
                ProgressSoundRules.ForQuestStateChange(
                    ProgressSoundRules.NotStarted, ProgressSoundRules.Active, "0"),
                Is.EqualTo(ProgressSound.QuestGiven));
        }

        // ── Completion: 1 → 2, and only for the parent ─────────────────────────

        [Test]
        public void WholeQuestCompleted_PlaysTheQuestOkChime()
        {
            ProgressSound sound = ProgressSoundRules.ForQuestStateChange(
                ProgressSoundRules.Active, ProgressSoundRules.Done, null);

            Assert.That(sound, Is.EqualTo(ProgressSound.QuestComplete));
        }

        [Test]
        public void ASubObjectiveClosing_IsSilent()
        {
            // `if(!this.sub) { … "quest_ok" } else { … "doneStage" }` (Quest.as:690-698). The
            // CampaignManager's publish carries `outcome.IsParent ? null : outcome.SubId`
            // (CampaignManager.cs:1699-1700), so a named stage is the only signal that separates the
            // two, and a sub must not reach quest_ok.
            ProgressSound sound = ProgressSoundRules.ForQuestStateChange(
                ProgressSoundRules.Active, ProgressSoundRules.Done, "2");

            Assert.That(sound, Is.EqualTo(ProgressSound.None),
                "A sub-objective closing is silent in the Oracle — it prints `doneStage` only.");
        }

        [Test]
        public void ASubObjectiveClosing_IsSilentForEveryStageSpelling()
        {
            foreach (string stage in new[] { "0", "1", "2", "3", "", null })
            {
                string probe = stage;
                ProgressSound sound = ProgressSoundRules.ForQuestStateChange(
                    ProgressSoundRules.Active, ProgressSoundRules.Done, probe);

                // null and "" are the SAME case to the rule: the port's publish passes
                // `IsParent ? null : SubId`, and a sub id is never empty, so both null and "" mean
                // "the parent moved". Anything else means "a sub moved" and must be silent.
                ProgressSound expected = string.IsNullOrEmpty(stage)
                    ? ProgressSound.QuestComplete
                    : ProgressSound.None;

                Assert.That(sound, Is.EqualTo(expected),
                    "stage='" + stage + "' was routed as " + sound);
            }
        }

        [Test]
        public void AlreadyDone_MovingAgain_IsSilent()
        {
            // `close()` is idempotent on state 2 (Quest.as:608-611), but a re-published 2 → 2 must
            // not double the chime.
            Assert.That(
                ProgressSoundRules.ForQuestStateChange(
                    ProgressSoundRules.Done, ProgressSoundRules.Done, null),
                Is.EqualTo(ProgressSound.QuestComplete),
                "A 2 → 2 with no stage is still the parent's completion.");

            Assert.That(
                ProgressSoundRules.ForQuestStateChange(
                    ProgressSoundRules.Done, ProgressSoundRules.Done, "1"),
                Is.EqualTo(ProgressSound.None));
        }

        // ── Transitions the Oracle never chimes on ────────────────────────────

        [Test]
        public void ProgressWithinAQuest_IsSilent()
        {
            // A collect objective ticking over is a counter change, not a state change: AS3 plays
            // nothing when `checkQuests` recomputes a sub (Game.as:634-651). This is the path that
            // produced the "0/20 cogwheel" report, and chiming on it would have made the bug louder.
            Assert.That(
                ProgressSoundRules.ForQuestStateChange(
                    ProgressSoundRules.Active, ProgressSoundRules.Active, "0"),
                Is.EqualTo(ProgressSound.None));
        }

        [Test]
        public void Failure_IsSilent()
        {
            Assert.That(
                ProgressSoundRules.ForQuestStateChange(1, 1, null),
                Is.EqualTo(ProgressSound.None));
        }
    }
}
