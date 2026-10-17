using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using PFE.Systems.RPG;
using PFE.Systems.RPG.Data;
using PFE.Tests.Editor.Common;
using PFE.UI.PipBuck;
using UnityEngine;

namespace PFE.Tests.EditMode.UI
{
    /// <summary>
    /// Pins the PipBuck's character sheet (item I10) — the row rules of
    /// <c>PipPageStat.setSubPages</c> that <see cref="PipStatList"/> ports.
    ///
    /// <para><b>Why the rules and not the page.</b> <c>PipPageStatView</c> is plumbing: it builds 18 row
    /// clips and writes two strings and a bar into each. Its only real decisions are the value column's
    /// per-sub-page x and the perk picker's <c>alpha</c> guard, and both are asserted here through the
    /// model they read. Everything the page cannot be checked on from a shell — whether the text lands
    /// where the eye expects — needs the editor, which is owner-only. What <i>can</i> be checked is the
    /// part that branches: which rows a snapshot produces, what each one says, and which of the four
    /// <c>@tip</c> formats applies.</para>
    ///
    /// <para><b>The reader is not under test.</b> <c>PlayerPipStatSource</c> needs a live
    /// <c>CharacterStats</c>, a <c>UnitStats</c> and a <c>GameInventory</c> — two of them
    /// <c>MonoBehaviour</c>s and one needing a <c>ScriptableObject</c> definition, all of which need the
    /// editor. It is deliberately thin for that reason, and every rule with a branch lives in
    /// <see cref="PipStatList"/>. The decisions the reader does own are recorded on the reader itself and
    /// are the things this fixture cannot see: which <c>tip == 4</c> digit maps to which
    /// <c>DamageType</c>, that a maxed perk is excluded from the picker rather than dimmed, and the four
    /// variables it returns <c>null</c> for.</para>
    ///
    /// <para><b>Two halves, and the second is the one that matters.</b> The first half runs against
    /// <see cref="PipTextTable.Empty"/> and pins the <i>rules</i> — the gates, the four formats, the
    /// sorts, the bar fractions. A rule asserted only through real captions cannot tell "the rule ran"
    /// from "the caption happened to match". The second half runs against the shipped
    /// <c>text_en.xml</c> and pins the <i>document</i>: that every key this page looks up exists, and
    /// that the composed values are the ones a player would read. That second half is what caught the
    /// resistance split below being wrong in the first draft.</para>
    /// </summary>
    [TestFixture]
    public class PipBuckStatTests
    {
        // ── Builders ─────────────────────────────────────────────────────────

        /// <summary>
        /// A snapshot whose param-table variables come from a dictionary. A name the dictionary lacks
        /// resolves to <c>null</c>, which is the Oracle's "нет переменной" path and pushes no row —
        /// so a test can state an absence by simply not supplying the key.
        /// </summary>
        private static PipStatSnapshot Snapshot(Dictionary<string, float> variables = null,
            Dictionary<string, float> resistances = null)
        {
            return new PipStatSnapshot
            {
                Variable = (name, tip) =>
                {
                    if (tip == 4)
                    {
                        return resistances != null && resistances.TryGetValue(name, out float r)
                            ? r
                            : (float?)null;
                    }

                    return variables != null && variables.TryGetValue(name, out float v) ? v : (float?)null;
                },
            };
        }

        private static List<PipStatRow> Rows(PipStatSnapshot snapshot, int subPage)
            => PipStatList.RowsFor(snapshot, subPage, PipTextTable.Empty);

        private static string[] Ids(IEnumerable<PipStatRow> rows) => rows.Select(r => r.Id).ToArray();

        private static string[] Values(IEnumerable<PipStatRow> rows) => rows.Select(r => r.Value).ToArray();

        /// <summary>The value of the single row with this id, or a failure that names what was there.</summary>
        private static string ValueOf(IEnumerable<PipStatRow> rows, string id)
        {
            PipStatRow[] matches = rows.Where(r => r.Id == id).ToArray();
            Assert.That(matches.Length, Is.EqualTo(1),
                "Expected exactly one row with id '" + id + "'. Rows present: [" + string.Join(", ", Ids(rows)) + "]");
            return matches[0].Value;
        }

        // ── Sub-page 1: the five fixed rows (PipPageStat.as:68-89) ────────────

        [Test]
        public void Main_FirstThreeRowsCarryNoId()
        {
            // PipPageStat.as:68-89 pushes these with only `nazv` and `lvl`, and setStatItem writes an
            // empty id for them (:483-486). An id invented here would make the info pane look up a
            // description for a row that has none.
            var rows = Rows(Snapshot(), PipStatList.MainSubPage);

            Assert.That(rows[0].Id, Is.Empty);
            Assert.That(rows[1].Id, Is.Empty);
            Assert.That(rows[2].Id, Is.Empty);
            Assert.That(rows[3].Id, Is.EqualTo("diff"));
            Assert.That(rows[4].Id, Is.EqualTo("reput"));
        }

        [Test]
        public void Main_XpRowIsCurrentPlusTheRemainder()
        {
            // `gg.pers.xpCur + " (" + (gg.pers.xpNext - gg.pers.xpCur) + ")"` — the second number is
            // what is STILL NEEDED, not the total for the next level. Reading it as the total is the
            // obvious mistake and would show a number four times too large at low levels.
            var snapshot = Snapshot();
            snapshot.XpCur = 1200;
            snapshot.XpNext = 5000;

            var rows = Rows(snapshot, PipStatList.MainSubPage);

            Assert.That(rows[2].Value, Is.EqualTo("1200 (3800)"));
        }

        [Test]
        public void Main_LevelAndNameAreTheSourceValues()
        {
            var snapshot = Snapshot();
            snapshot.PlayerName = "Littlepip";
            snapshot.Level = 7;

            var rows = Rows(snapshot, PipStatList.MainSubPage);

            Assert.That(rows[0].Value, Is.EqualTo("Littlepip"));
            Assert.That(rows[1].Value, Is.EqualTo("7"));
        }

        // ── The reputation tier (Pers.repTex, Pers.as:2391-2413) ─────────────

        [Test]
        public void ReputationTier_UsesTheOraclesThresholds()
        {
            // The five thresholds are private fields in the Oracle (rep1=10, rep2=20, rep3=35,
            // rep4=50, repGood=70) and are transcribed into PipStatList. Each boundary is asserted on
            // both sides, because an off-by-one here would silently relabel a player's standing.
            Assert.That(PipStatList.ReputationTier(0), Is.EqualTo("0"));
            Assert.That(PipStatList.ReputationTier(9), Is.EqualTo("0"));
            Assert.That(PipStatList.ReputationTier(10), Is.EqualTo("1"));
            Assert.That(PipStatList.ReputationTier(19), Is.EqualTo("1"));
            Assert.That(PipStatList.ReputationTier(20), Is.EqualTo("2"));
            Assert.That(PipStatList.ReputationTier(34), Is.EqualTo("2"));
            Assert.That(PipStatList.ReputationTier(35), Is.EqualTo("3"));
            Assert.That(PipStatList.ReputationTier(49), Is.EqualTo("3"));
            Assert.That(PipStatList.ReputationTier(50), Is.EqualTo("4"));
            Assert.That(PipStatList.ReputationTier(69), Is.EqualTo("4"));
            Assert.That(PipStatList.ReputationTier(70), Is.EqualTo("max"));
            Assert.That(PipStatList.ReputationTier(9999), Is.EqualTo("max"));
        }

        [Test]
        public void ReputationTier_IsOrdered_SoNoTierCanShadowAnother()
        {
            // The thresholds must ascend, or the if-chain in ReputationTier would return a lower tier
            // for a higher reputation and the failure would read as "the caption is wrong".
            Assert.That(PipStatList.Reputation1, Is.LessThan(PipStatList.Reputation2));
            Assert.That(PipStatList.Reputation2, Is.LessThan(PipStatList.Reputation3));
            Assert.That(PipStatList.Reputation3, Is.LessThan(PipStatList.Reputation4));
            Assert.That(PipStatList.Reputation4, Is.LessThan(PipStatList.ReputationGood));
        }

        [Test]
        public void Main_ReputationRow_DerivesTheTierKeyWhenTheSourceSuppliesNone()
        {
            // A fresh character has rep 0 and the Oracle renders `0 (Mysterious stranger)` — but that
            // word lives in the localisation document, which this half deliberately does not have. So
            // the assertion is on the KEY the model chose, not on the caption: PipTextTable's
            // missing-key marker echoes the id back, which makes the derivation observable for free.
            // `Shipped_MainSubPageReadsAsThePlayerWouldSeeIt` asserts the resolved caption.
            var snapshot = Snapshot();
            snapshot.Reputation = 0;

            var rows = Rows(snapshot, PipStatList.MainSubPage);

            Assert.That(ValueOf(rows, "reput"), Is.EqualTo("0 (*p_reput0)"));
        }

        [Test]
        public void Main_ReputationRow_ClimbsTheTiersAsReputationGrows()
        {
            // ReputationTier's thresholds are covered directly above; this drives the same ladder
            // through the row builder, so a wiring slip between the two (a stale tier, a dropped
            // parenthesis) still shows. Each value sits exactly on a threshold in Pers.as:2393-2413.
            var expected = new[]
            {
                (Reputation: 0, Tier: "0"),
                (Reputation: 10, Tier: "1"),
                (Reputation: 20, Tier: "2"),
                (Reputation: 35, Tier: "3"),
                (Reputation: 50, Tier: "4"),
                (Reputation: 70, Tier: "max"),
            };

            foreach ((int reputation, string tier) in expected)
            {
                var snapshot = Snapshot();
                snapshot.Reputation = reputation;

                var rows = Rows(snapshot, PipStatList.MainSubPage);

                Assert.That(ValueOf(rows, "reput"), Is.EqualTo(reputation + " (*p_reput" + tier + ")"),
                    "reputation " + reputation + " should read the reput" + tier + " caption");
            }
        }

        [Test]
        public void Main_ReputationRow_PrefersASourceSuppliedTierWord()
        {
            // The override exists for a future reputation system that knows the tier directly. It must
            // win, or that system's own caption would be silently replaced by a derived one.
            var snapshot = Snapshot();
            snapshot.Reputation = 42;
            snapshot.ReputationText = "Hand-picked";

            var rows = Rows(snapshot, PipStatList.MainSubPage);

            Assert.That(ValueOf(rows, "reput"), Is.EqualTo("42 (Hand-picked)"));
        }

        // ── The param table's gates (PipPageStat.as:95-99) ───────────────────

        [Test]
        public void ParamTable_EveryEntryIsGated_SoTheShowGuardIsNotDeadCode()
        {
            // A structural fact about the transcribed table: every entry has show >= 1. If one were 0,
            // the `show <= 0` guard would silently drop a row the Oracle also drops — but the guard
            // would then be load-bearing rather than defensive, and the two cases must not be confused.
            Assert.That(PipStatList.ParamTable.All(p => p.Show >= 1), Is.True,
                "A param entry with show == 0 would change the guard in AppendParamRows from defensive to load-bearing.");
        }

        [Test]
        public void ParamTable_OnlyAqualNeedsArmour_AndOnlyPinkNeedsTheStoryTrigger()
        {
            // The two conditional gates. Asserted as a structural fact so that a future table edit that
            // adds a show='2' row cannot land without this test naming it.
            string[] needsArmour = PipStatList.ParamTable.Where(p => p.Show == 2).Select(p => p.Id).ToArray();
            string[] needsStory = PipStatList.ParamTable.Where(p => p.Show == 3).Select(p => p.Id).ToArray();

            Assert.That(needsArmour, Is.EqualTo(new[] { "aqual" }));
            Assert.That(needsStory, Is.EqualTo(new[] { "pink" }));
        }

        [Test]
        public void Param_ShowTwo_HidesWhenNoArmourIsRead()
        {
            // `gg.armor == 0 && gg.marmor == 0` hides it. The port cannot read either, so the flag is
            // false and the row must not appear even though its variable is supplied.
            var variables = new Dictionary<string, float> { { "armor_qual", 0.75f } };

            var withoutArmour = Rows(Snapshot(variables), PipStatList.MainSubPage);
            Assert.That(Ids(withoutArmour), Does.Not.Contain("aqual"));

            var snapshot = Snapshot(variables);
            snapshot.ArmourPresent = true;
            var withArmour = Rows(snapshot, PipStatList.MainSubPage);
            Assert.That(Ids(withArmour), Does.Contain("aqual"));
        }

        [Test]
        public void Param_ShowThree_HidesUntilTheStoryTriggerIsSet()
        {
            // `!(xml.@show == "3" && !triggers["story_canter"] > 0)`. AS3's precedence makes the
            // trigger comparison the operand of `!`, so the effect is "hidden until the trigger is
            // set" — which is what the port writes. A reader who parses it the other way would show
            // the row only BEFORE the trigger, which is the opposite.
            var resistances = new Dictionary<string, float> { { "19", 0.9f } };

            var before = Rows(Snapshot(null, resistances), PipStatList.MainSubPage);
            Assert.That(Ids(before), Does.Not.Contain("pink"));

            var snapshot = Snapshot(null, resistances);
            snapshot.StoryCanter = true;
            var after = Rows(snapshot, PipStatList.MainSubPage);
            Assert.That(Ids(after), Does.Contain("pink"));
        }

        [Test]
        public void Param_MissingVariableProducesNoRow_RatherThanAZero()
        {
            // PipPageStat.as:117-118 — the Oracle traces and `continue`s. The port must do the same:
            // a zero is a claim about the player, an absent row is a claim about the port, and only
            // one of the two is true. This is the test that would fail if the resolver ever defaulted
            // to 0f instead of null.
            var rows = Rows(Snapshot(), PipStatList.MainSubPage);

            // None of the param-table ids may appear: the snapshot supplies no variable at all.
            string[] paramIds = PipStatList.ParamTable.Where(p => !p.IsSection).Select(p => p.Id).ToArray();
            foreach (string id in paramIds)
            {
                Assert.That(Ids(rows), Does.Not.Contain(id), "Row '" + id + "' appeared with no variable.");
            }
        }

        [Test]
        public void Param_SectionHeadersHaveNoValueAndNoPrefix()
        {
            // The three `@v=''` entries render as a bare label. The "-  " prefix is applied INSIDE the
            // `@v != ""` branch (:103-104), so a section header must not get it.
            var rows = Rows(Snapshot(), PipStatList.MainSubPage);

            foreach (string id in new[] { "param_atk", "param_def", "param_oth" })
            {
                PipStatRow section = rows.Single(r => r.Id == id);
                Assert.That(section.Value, Is.Empty);
                Assert.That(section.Name, Does.Not.StartWith("-  "));
            }
        }

        [Test]
        public void Param_ValueRowsCarryTheTwoSpaceDashPrefix()
        {
            var variables = new Dictionary<string, float> { { "maxOd", 75f } };
            var rows = Rows(Snapshot(variables), PipStatList.MainSubPage);

            PipStatRow od = rows.Single(r => r.Id == "od");
            Assert.That(od.Name, Does.StartWith("-  "));
            Assert.That(od.Name, Does.EndWith("*p_od"));   // the empty table's missing-key marker
        }

        // ── The four @tip formats (PipPageStat.as:122-162) ───────────────────

        [Test]
        public void Tip0_HidesAtZeroAndBelow_ShowsTheRawNumberAbove()
        {
            // `if(param > 0)`. Zero and negatives are hidden — this is the branch that keeps `skin`
            // and the armour lines off a fresh character.
            var atZero = Rows(Snapshot(new Dictionary<string, float> { { "maxOd", 0f } }), PipStatList.MainSubPage);
            Assert.That(Ids(atZero), Does.Not.Contain("od"));

            var below = Rows(Snapshot(new Dictionary<string, float> { { "maxOd", -5f } }), PipStatList.MainSubPage);
            Assert.That(Ids(below), Does.Not.Contain("od"));

            var above = Rows(Snapshot(new Dictionary<string, float> { { "maxOd", 2.5f } }), PipStatList.MainSubPage);
            Assert.That(ValueOf(above, "od"), Is.EqualTo("2.5"));
        }

        [Test]
        public void Tip1_HidesAtExactlyOne_ElseShowsTheSignedDelta()
        {
            // `if(param != 1 || ...)`. The exact-equality test is what makes a default character's
            // Attack block nearly empty: every damage multiplier starts at 1.0.
            var atOne = Rows(Snapshot(new Dictionary<string, float> { { "allDamMult", 1f } }), PipStatList.MainSubPage);
            Assert.That(Ids(atOne), Does.Not.Contain("alldamage"));

            var up = Rows(Snapshot(new Dictionary<string, float> { { "allDamMult", 1.1f } }), PipStatList.MainSubPage);
            Assert.That(ValueOf(up, "alldamage"), Is.EqualTo("+10%"));

            var down = Rows(Snapshot(new Dictionary<string, float> { { "allDamMult", 0.5f } }), PipStatList.MainSubPage);
            Assert.That(ValueOf(down, "alldamage"), Is.EqualTo("-50%"));
        }

        [Test]
        public void Tip1_JustUnderOne_PrintsTheMagnitudeWithNoSign()
        {
            // PipPageStat.as:140 is `(param >= 1 ? "+" : "") + Res.numb((param - 1) * 100) + "%"`.
            // Two things follow that a port which "prints the delta as a percentage" gets wrong:
            //
            //   * the prefix is chosen from `param`, NOT from the sign of the delta, so a param just
            //     under 1 gets no prefix at all; and
            //   * the magnitude goes through Res.numb, whose negative branch (Res.as:355-358) drops the
            //     sign: `Res.numb(-0.1)` is `Math.ceil(-1/10) + "." + Math.abs(-1 % 10)` = "0.1".
            //
            // So the row is a bare "0.1%". Note this does NOT generalise to "-50%" for param 0.5 —
            // there `Res.numb(-50)` takes the `% 10 == 0` branch (Res.as:351-354), which stringifies
            // the negative whole and keeps its minus. The quirk is real and lives in the two branches;
            // `Tip1_HidesAtExactlyOne_ElseShowsTheSignedDelta` pins the other side of it.
            var rows = Rows(Snapshot(new Dictionary<string, float> { { "allDamMult", 0.999f } }), PipStatList.MainSubPage);

            Assert.That(ValueOf(rows, "alldamage"), Is.EqualTo("0.1%"));
        }

        [Test]
        public void Tip2_AlwaysShowsTheFractionAsAPercent()
        {
            // `Res.numb(param * 100) + "%"`, with no gate at all.
            var rows = Rows(Snapshot(new Dictionary<string, float> { { "stamRun", 0.35f } }), PipStatList.MainSubPage);

            Assert.That(ValueOf(rows, "stamrun"), Is.EqualTo("35%"));
        }

        [Test]
        public void Tip2_ShowsZeroRatherThanHiding()
        {
            // tip 2 has no guard, so a zero must still produce a row — unlike tip 0. Reading the two
            // as the same "hide at zero" rule is the easy mistake.
            var rows = Rows(Snapshot(new Dictionary<string, float> { { "stamRun", 0f } }), PipStatList.MainSubPage);

            Assert.That(ValueOf(rows, "stamrun"), Is.EqualTo("0%"));
        }

        [Test]
        public void Tip3And4_ShowTheREDUCTION_AndHideAtExactlyOne()
        {
            // The reversal is the point: a multiplier below 1 means the player takes LESS damage, so
            // 0.99 reads "+1%" and 1.2 reads "-20%". A port that printed the multiplier as a
            // percentage would show "-1%" and "+20%" — plausible, and backwards.
            var atOne = Rows(Snapshot(new Dictionary<string, float> { { "allVulnerMult", 1f } }), PipStatList.MainSubPage);
            Assert.That(Ids(atOne), Does.Not.Contain("allresist"));

            var less = Rows(Snapshot(new Dictionary<string, float> { { "allVulnerMult", 0.99f } }), PipStatList.MainSubPage);
            Assert.That(ValueOf(less, "allresist"), Is.EqualTo("+1%"));

            var more = Rows(Snapshot(new Dictionary<string, float> { { "allVulnerMult", 1.2f } }), PipStatList.MainSubPage);
            Assert.That(ValueOf(more, "allresist"), Is.EqualTo("-20%"));
        }

        [Test]
        public void Tip4_ReadsTheVulnerabilityTable_NotANamedStat()
        {
            // The one tip whose `@v` is a damage-type INDEX rather than a variable name. The table
            // entry for `bullet` carries "0"; the resolver is asked with tip == 4 and must read the
            // resistance table, not look up a stat called "0".
            //
            // The two candidate tables are given wildly different values so a misread is unmistakable:
            // taking the variables map would yield Res.numb((1 - 999) * 100) = "-99800%".
            //
            // The expected sign is "+" and not "-": PipPageStat.as:159 is
            // `(param < 1 ? "+" : "") + Res.numb((1 - param) * 100) + "%"`, so a resistance BELOW 1
            // (less damage taken) is the positive case. Reading the multiplier as the number to print
            // gives "-20%" — plausible, and backwards.
            var variables = new Dictionary<string, float> { { "0", 999f } };      // the WRONG table
            var resistances = new Dictionary<string, float> { { "0", 0.8f } };

            var rows = Rows(Snapshot(variables, resistances), PipStatList.MainSubPage);

            Assert.That(ValueOf(rows, "bullet"), Is.EqualTo("+20%"));
        }

        [Test]
        public void Tip4_MapsEveryShippedIndexToADistinctDamageType()
        {
            // A structural fact: the digits the table carries are unique, so no two resistance rows can
            // read the same entry. Duplicated digits would make two damage types show one number and
            // the list would look right.
            int[] digits = PipStatList.ParamTable
                .Where(p => p.Tip == 4)
                .Select(p => int.Parse(p.Variable))
                .ToArray();

            Assert.That(digits.Length, Is.EqualTo(14));
            Assert.That(digits.Distinct().Count(), Is.EqualTo(digits.Length));
        }

        // ── Sub-page 2: skills (PipPageStat.as:299-322, 511-547) ─────────────

        [Test]
        public void SkillTier_MatchesTheOraclesFiveBoundaries()
        {
            // Pers.getSkLevel. Asserted at each boundary because the tier is what the row PRINTS — the
            // level only shows through the pip bar — so an off-by-one here is a visibly wrong number.
            Assert.That(PipStatList.SkillTier(0), Is.EqualTo(0));
            Assert.That(PipStatList.SkillTier(1), Is.EqualTo(0));
            Assert.That(PipStatList.SkillTier(2), Is.EqualTo(1));
            Assert.That(PipStatList.SkillTier(4), Is.EqualTo(1));
            Assert.That(PipStatList.SkillTier(5), Is.EqualTo(2));
            Assert.That(PipStatList.SkillTier(8), Is.EqualTo(2));
            Assert.That(PipStatList.SkillTier(9), Is.EqualTo(3));
            Assert.That(PipStatList.SkillTier(13), Is.EqualTo(3));
            Assert.That(PipStatList.SkillTier(14), Is.EqualTo(4));
            Assert.That(PipStatList.SkillTier(19), Is.EqualTo(4));
            Assert.That(PipStatList.SkillTier(20), Is.EqualTo(5));
            Assert.That(PipStatList.SkillTier(999), Is.EqualTo(5));
        }

        [Test]
        public void Skills_RegularSkillPrintsItsTier_NotItsLevel()
        {
            // :526 — `param1.numb.text = this.pers.getSkLevel(param2.lvl)`. The level is carried by the
            // twenty pips, so the number beside them is the tier. Printing the level would show "20"
            // next to a full bar and read as a different stat.
            var snapshot = Snapshot();
            snapshot.Skills = new[] { new PipStatSkill("melee", 20, false, 0, 0) };

            var rows = Rows(snapshot, PipStatList.SkillsSubPage);

            Assert.That(ValueOf(rows, "melee"), Is.EqualTo("5"));
        }

        [Test]
        public void Skills_PostSkillPrintsLevelRemainderAndTier()
        {
            // :521 — `lvl + "  (+" + (postSkTab[tier] - lvl) + ")  " + Res.pipText("level") + ": " + tier`.
            // The `(+n)` is what is LEFT to the tier's ceiling, which is why it shrinks as the level
            // rises; reading it as progress made would invert it.
            var snapshot = Snapshot();
            snapshot.Level = 25;
            snapshot.Skills = new[] { new PipStatSkill("attack", 25, true, 3, 26) };

            var rows = Rows(snapshot, PipStatList.SkillsSubPage);

            // PipTextTable.Empty has no `level` key, so the caption degrades to the Oracle's marker.
            Assert.That(ValueOf(rows, "attack"), Is.EqualTo("25  (+1)  *p_level: 3"));
        }

        [Test]
        public void Skills_PostSkillIsHiddenBelowLevelTwenty()
        {
            // :307 — `!(this.pers.level < Pers.postPersLevel && sk.post > 0)`. The three post-game
            // skills do not exist before level 20, so the row must be absent rather than zero.
            var below = Snapshot();
            below.Level = 19;
            below.Skills = new[] { new PipStatSkill("knowl", 30, true, 4, 35) };
            Assert.That(Ids(Rows(below, PipStatList.SkillsSubPage)), Does.Not.Contain("knowl"));

            var at = Snapshot();
            at.Level = 20;
            at.Skills = new[] { new PipStatSkill("knowl", 30, true, 4, 35) };
            Assert.That(Ids(Rows(at, PipStatList.SkillsSubPage)), Does.Contain("knowl"));
        }

        [Test]
        public void Skills_RegularSkillIsShownAtEveryLevel()
        {
            // The mirror of the test above: the level-20 gate must apply to post skills ONLY. A guard
            // that leaked onto regular skills would blank most of the page at level 1.
            var snapshot = Snapshot();
            snapshot.Level = 1;
            snapshot.Skills = new[] { new PipStatSkill("melee", 0, false, 0, 0) };

            Assert.That(Ids(Rows(snapshot, PipStatList.SkillsSubPage)), Does.Contain("melee"));
        }

        [Test]
        public void Skills_BarIsTheLevelOverTwenty()
        {
            // :527-544 — the twenty pips. The port draws one continuous fill of the same fraction, so
            // the fraction is the part worth pinning.
            var snapshot = Snapshot();
            snapshot.Skills = new[] { new PipStatSkill("melee", 15, false, 0, 0) };

            var rows = Rows(snapshot, PipStatList.SkillsSubPage);

            Assert.That(rows.Single(r => r.Id == "melee").Bar, Is.EqualTo(0.75f).Within(1e-6f));
            Assert.That(rows.Single(r => r.Id == "melee").HasBar, Is.True);
        }

        // ── Sub-page 3: perks (PipPageStat.as:323-362) ───────────────────────

        [Test]
        public void Perks_SortOnSortKeyThenLocalisedName()
        {
            // `arr.sortOn(["sort","nazv"])`. The two-key sort is the point: sorting on the name alone
            // would bury the perk the player can actually take, which is what `sort` exists to lift.
            var snapshot = Snapshot();
            snapshot.Perks = new[]
            {
                new PipStatPerk("zeta", 1, 1, 2, false),    // late key, late name
                new PipStatPerk("alpha", 1, 1, 2, false),   // late key, early name
                new PipStatPerk("beta", 1, 1, 1, false),    // early key
            };

            var rows = Rows(snapshot, PipStatList.PerksSubPage);

            // sort 1 first, then sort 2 ordered by name. With PipTextTable.Empty every name degrades
            // to "*e_<id>", which still orders by id — so the expectation is stated in ids.
            Assert.That(Ids(rows), Is.EqualTo(new[] { "beta", "alpha", "zeta" }));
        }

        [Test]
        public void Perks_ValueIsTheRank()
        {
            var snapshot = Snapshot();
            snapshot.Perks = new[] { new PipStatPerk("dexter", 3, 3, 1, false) };

            var rows = Rows(snapshot, PipStatList.PerksSubPage);

            Assert.That(ValueOf(rows, "dexter"), Is.EqualTo("3"));
        }

        // ── Sub-page 4: effects (PipPageStat.as:363-410) ─────────────────────

        [Test]
        public void Effects_ForeverShowsInfinity_TimedShowsSeconds()
        {
            // :374, :390-393. `∞` is the default and is only replaced when the effect is not forever —
            // so a timed effect with 0 ticks left reads "0", not "∞".
            var snapshot = Snapshot();
            snapshot.Effects = new[]
            {
                new PipStatEffect("buff", forever: true, seconds: 0, level: 1, isAddiction: false, isFood: false, isDrunk: false),
                new PipStatEffect("timer", forever: false, seconds: 45, level: 1, isAddiction: false, isFood: false, isDrunk: false),
                new PipStatEffect("expired", forever: false, seconds: 0, level: 1, isAddiction: false, isFood: false, isDrunk: false),
            };

            var rows = Rows(snapshot, PipStatList.EffectsSubPage);

            Assert.That(ValueOf(rows, "buff"), Is.EqualTo("∞"));
            Assert.That(ValueOf(rows, "timer"), Is.EqualTo("45"));
            Assert.That(ValueOf(rows, "expired"), Is.EqualTo("0"));
        }

        [Test]
        public void Effects_AddictionRenamesOnlyWhenTheAdEntryExists()
        {
            // :376-384 — the rename happens only when `Res.txt("e", id + "_ad")` is non-empty. With no
            // document the entry does not exist, so the name must stay the plain one and the id must
            // NOT gain the `_ad` suffix. The suffix matters: it is what the info pane looks up.
            var snapshot = Snapshot();
            snapshot.Effects = new[]
            {
                new PipStatEffect("jet", forever: true, seconds: 0, level: 2, isAddiction: true, isFood: false, isDrunk: false),
            };

            var rows = Rows(snapshot, PipStatList.EffectsSubPage);

            Assert.That(Ids(rows), Is.EqualTo(new[] { "jet" }));
        }

        [Test]
        public void Effects_DrunkIsCaptionedByItsLevel()
        {
            // :385-389 — the one effect whose caption is chosen by `ef.lvl`. Every other effect is named
            // by its id.
            var snapshot = Snapshot();
            snapshot.Effects = new[]
            {
                new PipStatEffect("drunk", forever: true, seconds: 0, level: 3, isAddiction: false, isFood: false, isDrunk: true),
            };

            var rows = Rows(snapshot, PipStatList.EffectsSubPage);

            Assert.That(ValueOf(rows, "drunk"), Is.EqualTo("∞"));
            Assert.That(rows[0].Name, Is.EqualTo("*p_drunk3"));
        }

        [Test]
        public void Effects_FoodIsRenamedToTheSharedWord()
        {
            // :394-397 — `if(ef.tip == 3) n.nazv = this.n_food`. The Oracle reads that word from the
            // `e` table (:37), NOT from `pip`, which is why the port's lookup uses the `e` alias.
            var snapshot = Snapshot();
            snapshot.Effects = new[]
            {
                new PipStatEffect("apple", forever: false, seconds: 30, level: 1, isAddiction: false, isFood: true, isDrunk: false),
            };

            var rows = Rows(snapshot, PipStatList.EffectsSubPage);

            Assert.That(rows[0].Name, Is.EqualTo("*e_food"));
        }

        [Test]
        public void Effects_EmptyListProducesTheEmptyNotice()
        {
            var snapshot = Snapshot();

            Assert.That(Rows(snapshot, PipStatList.EffectsSubPage), Is.Empty);
            Assert.That(PipStatList.EmptyTextFor(PipStatList.EffectsSubPage, snapshot, PipTextTable.Empty),
                Is.EqualTo("*p_emptyeff"));
        }

        [Test]
        public void Effects_NonEmptyListSuppressesTheEmptyNotice()
        {
            var snapshot = Snapshot();
            snapshot.Effects = new[]
            {
                new PipStatEffect("buff", true, 0, 1, false, false, false),
            };

            Assert.That(PipStatList.EmptyTextFor(PipStatList.EffectsSubPage, snapshot, PipTextTable.Empty), Is.Null);
        }

        // ── Sub-page 5: health (PipPageStat.as:177-298) ──────────────────────

        private static PipStatSnapshot HealthSnapshot()
        {
            var snapshot = Snapshot();
            snapshot.Vitals = new PipStatVitals
            {
                Hp = 137f,
                MaxHp = 200f,
                HeadHp = 150f,
                TorsoHp = 100f,
                LegsHp = 50f,
                BloodHp = 200f,
                ManaOrganHp = 400f,
                InMaxHp = 200f,
                InMaxMana = 400f,
                HeadSt = 1,
                TorsoSt = 2,
                LegsSt = 3,
                BloodSt = 0,
                ManaSt = 0,
            };
            return snapshot;
        }

        [Test]
        public void Health_OrganRowIdsCarryTheTraumaStage()
        {
            // :201-230 — `"statHead" + gg.pers.headSt`. The digit is DATA: the info pane parses it back
            // out to pick the matching trauma perk (:625-680). An id without it would silently make
            // every trauma description unreachable.
            var rows = Rows(HealthSnapshot(), PipStatList.HealthSubPage);

            Assert.That(Ids(rows), Does.Contain("statHead1"));
            Assert.That(Ids(rows), Does.Contain("statTors2"));
            Assert.That(Ids(rows), Does.Contain("statLegs3"));
            Assert.That(Ids(rows), Does.Contain("statBlood0"));
            Assert.That(Ids(rows), Does.Contain("statMana0"));
        }

        [Test]
        public void Health_OrganRowsAreIndented_AndDividedByTheOrganCeiling()
        {
            // The three-space indent is the Oracle's (:202). The denominator is `inMaxHP` for the four
            // limb/blood organs and `inMaxMana` for the mana organ — using one for both would make the
            // mana row read 100 % on a full bar.
            var rows = Rows(HealthSnapshot(), PipStatList.HealthSubPage);

            PipStatRow head = rows.Single(r => r.Id == "statHead1");
            Assert.That(head.Name, Is.EqualTo("   *p_head"));
            Assert.That(head.Value, Is.EqualTo("150/200"));

            PipStatRow mana = rows.Single(r => r.Id == "statMana0");
            Assert.That(mana.Value, Is.EqualTo("400/400"));
            Assert.That(mana.Bar, Is.EqualTo(1f).Within(1e-6f));
        }

        [Test]
        public void Health_HpRowUsesTheUnitPool_AndCarriesABar()
        {
            // :190-195 — `gg.hp` / `gg.maxhp`, the UNIT's pool, not Pers's. The port keeps the same
            // split, so this row must read UnitStats's numbers while the organs read CharacterStats's.
            var rows = Rows(HealthSnapshot(), PipStatList.HealthSubPage);

            PipStatRow hp = rows.Single(r => r.Id == "hp");
            Assert.That(hp.Value, Is.EqualTo("137/200"));
            Assert.That(hp.Bar, Is.EqualTo(137f / 200f).Within(1e-6f));
        }

        [Test]
        public void Health_OrganismHeaderHasNoValueAndNoBar()
        {
            // :196-200 — a bare "Body condition:" label. It is a row, not a header, so it must not be
            // mistaken for one and hidden by the header rule.
            var rows = Rows(HealthSnapshot(), PipStatList.HealthSubPage);

            PipStatRow organism = rows.Single(r => r.Id == "organism");
            Assert.That(organism.Name, Is.EqualTo("*p_organism:"));
            Assert.That(organism.Value, Is.Empty);
            Assert.That(organism.HasBar, Is.False);
        }

        [Test]
        public void Health_ResistanceReadings_AppearWithoutTheExposureReadings()
        {
            // THE SPLIT. The port can read `vulner[Bleed]` and `vulner[Poison]` off the bound unit, so
            // these two rows are supplied even though the four radiation/wound/poison readings are not.
            // Gating them behind HasExposureReadings would omit two rows the port can honestly fill,
            // which is a false claim about the port.
            PipStatSnapshot snapshot = HealthSnapshot();
            snapshot.Vitals.HasResistanceReadings = true;
            snapshot.Vitals.ResBleeding = 0.75f;
            snapshot.Vitals.ResPoison = 0.5f;

            var rows = Rows(snapshot, PipStatList.HealthSubPage);

            Assert.That(ValueOf(rows, "resbleeding"), Is.EqualTo("25%"));
            Assert.That(ValueOf(rows, "respoison"), Is.EqualTo("50%"));
        }

        [Test]
        public void Health_ExposureReadings_NeverAppear_EvenWithResistances()
        {
            // The other half of the split: the four readings the port cannot take must stay absent
            // even when the resistance flag is on, so the two flags cannot be collapsed back together
            // without this test going red.
            PipStatSnapshot snapshot = HealthSnapshot();
            snapshot.Vitals.HasResistanceReadings = true;

            var rows = Rows(snapshot, PipStatList.HealthSubPage);

            foreach (string id in new[] { "rad", "radx", "cut", "poison" })
            {
                Assert.That(Ids(rows), Does.Not.Contain(id), "Row '" + id + "' has no port source.");
            }
        }

        [Test]
        public void Health_ExposureReadings_AppearWhenTheFlagIsSet()
        {
            // The flag is not dead: when a source can supply the four readings, they appear. This is
            // the control for the test above — without it, "the rows are absent" would also pass on a
            // page that had simply forgotten them.
            PipStatSnapshot snapshot = HealthSnapshot();
            snapshot.Vitals.HasExposureReadings = true;
            snapshot.Vitals.Rad = 12f;
            snapshot.Vitals.RadX = 0.9f;
            snapshot.Vitals.Cut = 3.5f;
            snapshot.Vitals.Poison = 0.5f;
            snapshot.Vitals.HasResistanceReadings = true;
            snapshot.Vitals.ResBleeding = 1f;
            snapshot.Vitals.ResPoison = 1f;

            var rows = Rows(snapshot, PipStatList.HealthSubPage);

            Assert.That(ValueOf(rows, "rad"), Is.EqualTo("12"));
            Assert.That(ValueOf(rows, "radx"), Is.EqualTo("10%"));
            Assert.That(ValueOf(rows, "cut"), Is.EqualTo("3.5"));
            Assert.That(ValueOf(rows, "poison"), Is.EqualTo("0.5"));
        }

        [Test]
        public void Health_AllSixExposureRowsKeepTheOraclesOrder()
        {
            // :231-260 pushes all six in one sequence. Because the two flags are independent, the
            // builder has to ask per row; gating them in two blocks would reorder the list whenever
            // the flags disagreed — which in this port they always do.
            PipStatSnapshot snapshot = HealthSnapshot();
            snapshot.Vitals.HasExposureReadings = true;
            snapshot.Vitals.HasResistanceReadings = true;

            string[] ids = Ids(Rows(snapshot, PipStatList.HealthSubPage)).ToArray();
            int rad = System.Array.IndexOf(ids, "rad");
            int radx = System.Array.IndexOf(ids, "radx");
            int cut = System.Array.IndexOf(ids, "cut");
            int bleeding = System.Array.IndexOf(ids, "resbleeding");
            int poison = System.Array.IndexOf(ids, "poison");
            int respoison = System.Array.IndexOf(ids, "respoison");

            Assert.That(rad, Is.LessThan(radx));
            Assert.That(radx, Is.LessThan(cut));
            Assert.That(cut, Is.LessThan(bleeding));
            Assert.That(bleeding, Is.LessThan(poison));
            Assert.That(poison, Is.LessThan(respoison));
        }

        [Test]
        public void Health_MedicineDisabledReplacesTheWholePageWithOneNotice()
        {
            // :179-184 — the Oracle sets `statHead.visible = false` and returns before pushing a single
            // row. So this is a page-level message, not an empty-list one, and BOTH halves must agree:
            // no rows, and the notice.
            PipStatSnapshot snapshot = HealthSnapshot();
            snapshot.Vitals.MedicineDisabled = true;

            Assert.That(Rows(snapshot, PipStatList.HealthSubPage), Is.Empty);
            Assert.That(PipStatList.EmptyTextFor(PipStatList.HealthSubPage, snapshot, PipTextTable.Empty),
                Is.EqualTo("*p_emptymed"));
        }

        [Test]
        public void Health_AddictionRowsAreSkippedAtOrBelowZero()
        {
            // :269-297 — `if(this.pers.addictions[i] > 0)`. An addiction that has worn off is not
            // listed, and the stage word is clamped to ad0..ad3.
            PipStatSnapshot snapshot = HealthSnapshot();
            snapshot.Vitals.Addictions = new[]
            {
                new PipStatAddiction("jet", 40f, 2),
                new PipStatAddiction("medx", 0f, 0),
            };

            var rows = Rows(snapshot, PipStatList.HealthSubPage);

            Assert.That(Ids(rows), Does.Contain("jet"));
            Assert.That(Ids(rows), Does.Not.Contain("medx"));
            Assert.That(ValueOf(rows, "jet"), Is.EqualTo("40% (*p_ad2)"));
        }

        [Test]
        public void Health_AddictionStageIsClampedToTheFourKnownWords()
        {
            // The Oracle's own if-chain covers ad0..ad3; a stage above 3 falls to `ad3` (:270-296).
            PipStatSnapshot snapshot = HealthSnapshot();
            snapshot.Vitals.Addictions = new[] { new PipStatAddiction("jet", 99f, 7) };

            var rows = Rows(snapshot, PipStatList.HealthSubPage);

            Assert.That(ValueOf(rows, "jet"), Is.EqualTo("99% (*p_ad3)"));
        }

        [Test]
        public void Health_PhoenixRowIsAbsentWhenThePortHasNoPet()
        {
            PipStatSnapshot snapshot = HealthSnapshot();
            snapshot.Vitals.HasPhoenix = false;
            Assert.That(Ids(Rows(snapshot, PipStatList.HealthSubPage)), Does.Not.Contain("phoenix"));

            snapshot.Vitals.HasPhoenix = true;
            snapshot.Vitals.PhoenixName = "Phoenix";
            snapshot.Vitals.PhoenixHp = 30f;
            snapshot.Vitals.PhoenixMaxHp = 50f;
            Assert.That(ValueOf(Rows(snapshot, PipStatList.HealthSubPage), "phoenix"), Is.EqualTo("30/50"));
        }

        // ── Sub-page 6: the perk picker (PipPageStat.as:411-449) ─────────────

        [Test]
        public void Picker_ValueIsTheRankThePerkWouldBecome()
        {
            // :436 — `lvl: numb + 1`. A perk the player has never taken reads "1", not "0" — the picker
            // previews the result of taking it.
            var snapshot = Snapshot();
            snapshot.PerkChoices = new[] { new PipStatPerk("dexter", 0, 1, 0, true) };

            var rows = Rows(snapshot, PipStatList.PerkPickSubPage);

            Assert.That(ValueOf(rows, "dexter"), Is.EqualTo("1"));
        }

        [Test]
        public void Picker_AppendsTheMaxRankOnlyForAMultiRankPerk()
        {
            // :502-505 — `if(maxlvl > 1 && maxlvl < 1000)`. A single-rank perk shows a bare "1"; a
            // three-rank perk shows "1/3". The upper bound is the Oracle's own guard against a sentinel.
            var snapshot = Snapshot();
            snapshot.PerkChoices = new[]
            {
                new PipStatPerk("single", 0, 1, 0, true),
                new PipStatPerk("triple", 0, 3, 0, true),
                new PipStatPerk("sentinel", 0, 1000, 0, true),
            };

            var rows = Rows(snapshot, PipStatList.PerkPickSubPage);

            Assert.That(ValueOf(rows, "single"), Is.EqualTo("1"));
            Assert.That(ValueOf(rows, "triple"), Is.EqualTo("1/3"));
            Assert.That(ValueOf(rows, "sentinel"), Is.EqualTo("1"));
        }

        [Test]
        public void Picker_UnselectableCandidateIsDimmedNotHidden()
        {
            // :550-553 — `if(!param2.ok) param1.alpha = 0.4`. The Oracle lists a perk the player cannot
            // take yet and dims it; hiding it instead would make the picker look like it had nothing to
            // offer. The alpha is also what the page's click guard reads.
            var snapshot = Snapshot();
            snapshot.PerkChoices = new[]
            {
                new PipStatPerk("ready", 0, 1, 0, true),
                new PipStatPerk("later", 0, 1, 1, false),
            };

            var rows = Rows(snapshot, PipStatList.PerkPickSubPage);

            Assert.That(rows.Single(r => r.Id == "ready").Alpha, Is.EqualTo(1f));
            Assert.That(rows.Single(r => r.Id == "later").Alpha, Is.EqualTo(0.4f).Within(1e-6f));
        }

        [Test]
        public void Picker_SortsSelectableCandidatesFirst()
        {
            // `sort: 1 - res` puts res == 1 (takeable now) at 0 and res == 0 at 1, so the perks the
            // player can actually choose float to the top. Inverting the sign is the easy mistake and
            // would bury them.
            var snapshot = Snapshot();
            snapshot.PerkChoices = new[]
            {
                new PipStatPerk("later", 0, 1, 1, false),
                new PipStatPerk("ready", 0, 1, 0, true),
            };

            var rows = Rows(snapshot, PipStatList.PerkPickSubPage);

            Assert.That(Ids(rows), Is.EqualTo(new[] { "ready", "later" }));
        }

        // ── Headers (PipPageStat.as:189, 303-304, 326-327, 365-367, 414-415) ──

        [Test]
        public void Header_IsTheOraclesKeyPerSubPage()
        {
            (string name, string value) main = PipStatList.HeaderFor(PipStatList.MainSubPage, PipTextTable.Empty);
            (string name, string value) skills = PipStatList.HeaderFor(PipStatList.SkillsSubPage, PipTextTable.Empty);
            (string name, string value) perks = PipStatList.HeaderFor(PipStatList.PerksSubPage, PipTextTable.Empty);
            (string name, string value) effects = PipStatList.HeaderFor(PipStatList.EffectsSubPage, PipTextTable.Empty);
            (string name, string value) health = PipStatList.HeaderFor(PipStatList.HealthSubPage, PipTextTable.Empty);

            // Main and Health set both to "" explicitly — the header is BLANK there, not missing.
            Assert.That(main, Is.EqualTo((string.Empty, string.Empty)));
            Assert.That(health, Is.EqualTo((string.Empty, string.Empty)));

            Assert.That(skills, Is.EqualTo(("*p_is1", "*p_is2")));
            Assert.That(perks, Is.EqualTo(("*p_is5", "*p_is2")));
            Assert.That(effects, Is.EqualTo(("*p_is3", "*p_is4")));
        }

        [Test]
        public void Header_PerksAndPickerShareOneHeader()
        {
            // Both branches assign is5 / is2 (:326-327 and :414-415). If they diverged, the level-up
            // page would lose its column headings.
            Assert.That(PipStatList.HeaderFor(PipStatList.PerkPickSubPage, PipTextTable.Empty),
                Is.EqualTo(PipStatList.HeaderFor(PipStatList.PerksSubPage, PipTextTable.Empty)));
        }

        // ── The bottom strip (PipPageStat.as:770-861) ────────────────────────

        [Test]
        public void Bottom_MainShowsThePlayClock()
        {
            var snapshot = Snapshot();
            snapshot.GameTime = "1:26:04";

            Assert.That(PipStatList.BottomText(snapshot, PipStatList.MainSubPage, PipTextTable.Empty),
                Is.EqualTo("*p_tgame: 1:26:04"));
        }

        [Test]
        public void Bottom_SkillsAndPerksShowTheirUnspentPointsInPink()
        {
            var snapshot = Snapshot();
            snapshot.SkillPoints = 5;
            snapshot.PerkPoints = 2;

            string skills = PipStatList.BottomText(snapshot, PipStatList.SkillsSubPage, PipTextTable.Empty);
            string perks = PipStatList.BottomText(snapshot, PipStatList.PerksSubPage, PipTextTable.Empty);

            // The count is wrapped in the Oracle's `pink()`. Asserted by structure rather than by hex,
            // so this test does not restate the colour table it is not testing.
            Assert.That(skills, Does.StartWith("*p_skillpoint: <color=#"));
            Assert.That(skills, Does.Contain(">5</color>"));
            Assert.That(perks, Does.StartWith("*p_perkpoint: <color=#"));
            Assert.That(perks, Does.Contain(">2</color>"));
        }

        [Test]
        public void Bottom_PickerPromptsUntilSomethingIsPicked()
        {
            // :786-796 — the instruction until a pick is made, then the perk's name in pink.
            var snapshot = Snapshot();

            Assert.That(PipStatList.BottomText(snapshot, PipStatList.PerkPickSubPage, PipTextTable.Empty),
                Is.EqualTo("*p_chooseperk"));

            snapshot.SelectedPerk = "dexter";
            string picked = PipStatList.BottomText(snapshot, PipStatList.PerkPickSubPage, PipTextTable.Empty);
            Assert.That(picked, Does.StartWith("<color=#"));
            Assert.That(picked, Does.Contain("*e_dexter"));
        }

        [Test]
        public void Bottom_HealthIsBlankUntilARowIsHovered()
        {
            // The strip names the medicine of the row UNDER THE POINTER (`infoItemId`, :797). Nothing
            // is selectable on this sub-page, so before a hover there is nothing to name.
            PipStatSnapshot snapshot = HealthSnapshot();
            snapshot.MedicineCounts = new Dictionary<string, int>();

            Assert.That(PipStatList.BottomText(snapshot, PipStatList.HealthSubPage, PipTextTable.Empty),
                Is.Empty);
        }

        [Test]
        public void Bottom_HealthNamesTheHoveredRowsMedicine_AndSumsTheHealthPotions()
        {
            // :800-803 — the `hp` row sums three potion ids under one label. Reading only `pot1` would
            // under-count every player who carries the stronger ones.
            PipStatSnapshot snapshot = HealthSnapshot();
            snapshot.HoveredRowId = "hp";
            snapshot.MedicineCounts = new Dictionary<string, int>
            {
                { "pot1", 2 }, { "pot2", 3 }, { "pot3", 1 }, { "antiradin", 4 },
            };

            string strip = PipStatList.BottomText(snapshot, PipStatList.HealthSubPage, PipTextTable.Empty);

            Assert.That(strip, Does.StartWith("*p_healpotions: "));
            Assert.That(strip, Does.Contain(">6</color>"));
        }

        [Test]
        public void Bottom_HealthNamesASingleMedicineForAnExposureRow()
        {
            PipStatSnapshot snapshot = HealthSnapshot();
            snapshot.HoveredRowId = "rad";
            snapshot.MedicineCounts = new Dictionary<string, int> { { "antiradin", 3 } };

            string strip = PipStatList.BottomText(snapshot, PipStatList.HealthSubPage, PipTextTable.Empty);

            Assert.That(strip, Does.StartWith("*i_antiradin: "));
            Assert.That(strip, Does.Contain(">3</color>"));
        }

        [Test]
        public void Bottom_HealthCountsAMissingMedicineAsZero()
        {
            // `inv.items[id].kol` on a missing row reads 0 in the Oracle. It must not throw, and it
            // must not silently omit the label — the row is still named, with a zero beside it.
            PipStatSnapshot snapshot = HealthSnapshot();
            snapshot.HoveredRowId = "rad";
            snapshot.MedicineCounts = new Dictionary<string, int>();

            string strip = PipStatList.BottomText(snapshot, PipStatList.HealthSubPage, PipTextTable.Empty);

            Assert.That(strip, Does.StartWith("*i_antiradin: "));
            Assert.That(strip, Does.Contain(">0</color>"));
        }

        [Test]
        public void Bottom_HealthNamesNoMedicineForARowThatUsesNone()
        {
            // The organ rows carry no medicine on this sub-page — their ids are `statHead<n>` and the
            // Oracle's prefix tests (:804-819) do not match them. A strip that invented a medicine here
            // would offer the player something the Oracle does not.
            PipStatSnapshot snapshot = HealthSnapshot();
            snapshot.HoveredRowId = "statHead1";
            snapshot.MedicineCounts = new Dictionary<string, int> { { "bloodpak", 9 } };

            Assert.That(PipStatList.BottomText(snapshot, PipStatList.HealthSubPage, PipTextTable.Empty),
                Is.Empty);
        }

        // ── Scrolling (PipPage.as:944-1002) ──────────────────────────────────

        [Test]
        public void Scroll_ShowsEighteenRowsAndClampsToTheLastFullWindow()
        {
            // The page shares the inventory's clip arithmetic rather than re-deriving it, so this pins
            // the SHARED rule at the character sheet's row count. 20 rows leaves a 2-row scroll.
            Assert.That(PipStatList.VisibleRows, Is.EqualTo(18));
            Assert.That(PipStatList.MaxScroll(18), Is.EqualTo(0));
            Assert.That(PipStatList.MaxScroll(19), Is.EqualTo(1));
            Assert.That(PipStatList.MaxScroll(20), Is.EqualTo(2));
            Assert.That(PipStatList.MaxScroll(5), Is.EqualTo(0));
        }

        [Test]
        public void Scroll_FirstRowShowsTheEntryAtTheOffset()
        {
            Assert.That(PipStatList.EntryIndexForRow(0, 20, 0), Is.EqualTo(0));
            Assert.That(PipStatList.EntryIndexForRow(0, 20, 2), Is.EqualTo(2));
            Assert.That(PipStatList.EntryIndexForRow(17, 20, 2), Is.EqualTo(19));

            // Past the end of a short list, the clip is empty — -1, not a wrapped index.
            Assert.That(PipStatList.EntryIndexForRow(5, 3, 0), Is.EqualTo(-1));
        }

        // ── The shipped document (text_en.xml) ───────────────────────────────
        //
        // Everything above runs against PipTextTable.Empty and pins the RULES. This half pins the
        // DOCUMENT: that every key the page looks up is really there, and that the composed strings are
        // what a player would read. A rule asserted only against the empty table cannot tell "the rule
        // ran" from "the caption happened to be missing anyway".

        private static PipTextTable _shipped;

        /// <summary>
        /// The shipped <c>text_en.xml</c>, parsed once. Lazy rather than <c>[OneTimeSetUp]</c>, because
        /// the offline wall's runner dispatches only <c>SetUp</c>/<c>TearDown</c> — a one-time set-up
        /// silently never runs there and every test then reads a null table. Same trap
        /// <c>PipTextTableTests</c> records.
        /// </summary>
        private static PipTextTable Shipped
        {
            get
            {
                if (_shipped != null) return _shipped;

                string path = Path.Combine(SourceLint.ProjectRoot(),
                    "Assets", "_PFE", "Data", "Resources", "Localisation", "text_en.xml");

                Assert.That(File.Exists(path), Is.True, "The localisation document is missing: " + path);

                _shipped = PipTextTable.Parse(File.ReadAllText(path));

                Assert.That(_shipped.IsEmpty, Is.False, "The document parsed to zero entries: " + path);

                return _shipped;
            }
        }

        [Test]
        public void Shipped_EveryParamLabelAndGateKeyExists()
        {
            // 41 labels and the two gate keys the table carries. A missing one shows as "*p_<id>" on
            // screen, which reads as a translation problem rather than a missing row in the document.
            foreach (PipStatParam param in PipStatList.ParamTable)
            {
                Assert.That(Shipped.TryGet("p", param.Id, 0, out _), Is.True,
                    "The param label '" + param.Id + "' is not in the document.");
            }
        }

        [Test]
        public void Shipped_EveryCaptionThisPageLooksUpExists()
        {
            string[] pipKeys =
            {
                "name", "level", "expa", "diff", "reput", "hp", "organism", "head", "tors", "legs",
                "blood", "mana", "rad", "radx", "cut", "poison", "resbleeding", "respoison",
                "ad0", "ad1", "ad2", "ad3", "healpotions", "tgame", "skillpoint", "perkpoint",
                "chooseperk", "emptymed", "emptyperk", "emptyeff", "is1", "is2", "is3", "is4", "is5",
                "reput0", "reput1", "reput2", "reput3", "reput4", "reputmax",
            };

            var missing = pipKeys.Where(k => !Shipped.TryGet("p", k, 0, out _)).ToArray();
            Assert.That(missing, Is.Empty, "Missing `pip` keys: " + string.Join(", ", missing));
        }

        [Test]
        public void Shipped_DifficultyComesFromTheGuiTable_NotPip()
        {
            // The row's value is `Res.guiText("dif" + globalDif)` (:83), and the `gui` table is the one
            // the alias "g" maps to. Looking it up in `pip` would return nothing and the row would read
            // "*p_dif2" — the failure mode this test exists for.
            for (int level = 0; level <= 4; level++)
            {
                Assert.That(Shipped.TryGet("g", "dif" + level, 0, out _), Is.True,
                    "The `gui` table has no dif" + level + " key.");
            }

            Assert.That(Shipped.TryGet("g", "dif2", 0, out string normal), Is.True);
            Assert.That(normal, Is.EqualTo("Normal"));

            // And the control: the same key is NOT in `pip`, so a port that used the wrong alias would
            // have been caught by the assertion above rather than by luck.
            Assert.That(Shipped.TryGet("p", "dif2", 0, out _), Is.False);
        }

        [Test]
        public void Shipped_SkillNamesComeFromTheEffTable_NotPip()
        {
            // The row's label is `Res.txt("e", sk.id)`, and the alias "e" maps to `eff`. Skill names in
            // the `pip` table would be a different set — `level` is in both, for instance, which is
            // exactly why the alias has to be pinned rather than assumed.
            Assert.That(Shipped.TryGet("e", "smallguns", 0, out string skill), Is.True);
            Assert.That(skill, Is.EqualTo("Firearms"));
        }

        [Test]
        public void Shipped_MainSubPageReadsAsThePlayerWouldSeeIt()
        {
            // The composed rows, with real captions. This is the assertion that caught the resistance
            // split: the four exposure readings are absent while the two resistances are present, and
            // the resulting list is exactly what the Oracle's page would show for this snapshot.
            PipStatSnapshot snapshot = Snapshot(new Dictionary<string, float> { { "allDamMult", 1.1f } });
            snapshot.PlayerName = "Littlepip";
            snapshot.Level = 3;
            snapshot.XpCur = 1200;
            snapshot.XpNext = 5000;
            snapshot.GlobalDifficulty = 2;
            snapshot.Reputation = 0;

            var rows = PipStatList.RowsFor(snapshot, PipStatList.MainSubPage, Shipped);

            Assert.That(rows[0].Name, Is.EqualTo("Name"));
            Assert.That(rows[0].Value, Is.EqualTo("Littlepip"));
            Assert.That(rows[1].Name, Is.EqualTo("Level"));
            Assert.That(rows[2].Name, Is.EqualTo("Experience"));
            Assert.That(rows[2].Value, Is.EqualTo("1200 (3800)"));
            Assert.That(rows[3].Name, Is.EqualTo("Difficulty level"));
            Assert.That(rows[3].Value, Is.EqualTo("Normal"));
            Assert.That(rows[4].Name, Is.EqualTo("Reputation"));
            Assert.That(rows[4].Value, Is.EqualTo("0 (Mysterious stranger)"));

            // The param block: the three section headers, then the one row that has a value.
            Assert.That(rows[5].Name, Is.EqualTo("Attack parameters"));
            Assert.That(rows[5].Value, Is.Empty);

            PipStatRow damage = rows.Single(r => r.Id == "alldamage");
            Assert.That(damage.Name, Is.EqualTo("-  Damage"));
            Assert.That(damage.Value, Is.EqualTo("+10%"));
        }
    }

    /// <summary>
    /// Pins the perk picker's <b>commit</b> — the half of <c>PipPageStat.as:1007-1018</c> that spends
    /// the point, reached through <see cref="IPipStatSource.TryCommitPerk"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the commit needed its own fixture.</b> <c>PipPageStatView.SelectPerk</c> recorded the
    /// pick and logged that the port had no accept button, so a selection reached the bottom strip and
    /// stopped there. The Oracle's accept runs <c>addPerk(this.selectedPerk, true)</c> — the <c>minus</c>
    /// form. <c>CharacterStats.AddPerk(id)</c> is the Oracle's <b>free</b> default, so a commit built on
    /// it would hand the player every perk in the game for nothing. That difference is the entire reason
    /// this seam exists, and it is what these cases pin.</para>
    /// </remarks>
    [TestFixture]
    public class PipPerkCommitTests
    {
        private GameObject _go;
        private CharacterStats _stats;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("PipPerkCommitTests");
            _stats = _go.AddComponent<CharacterStats>();

            var curve = ScriptableObject.CreateInstance<LevelCurve>();
            curve.baseHp = 100;
            curve.hpPerLevel = 15;
            curve.organHpPerLevel = 40;
            curve.baseOrganHp = 200;
            curve.skillPointsPerLevel = 5;
            _stats.Initialize(curve);
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
        }

        private PlayerPipStatSource Source() =>
            new PlayerPipStatSource(() => _stats, () => null, () => null);

        [Test]
        public void TryCommitPerk_SpendsExactlyOnePoint()
        {
            _stats.SetSkillLevel("melee", 2); // oak's requirement (AllData.as:5376)
            _stats.GrantPerkPoints(2);

            bool committed = Source().TryCommitPerk("oak");

            Assert.That(committed, Is.True, "A pick with points must commit");
            Assert.That(_stats.GetPerkRank("oak"), Is.EqualTo(1), "…and the perk must land");
            Assert.That(_stats.PerkPoints, Is.EqualTo(1), "…and exactly one point must be spent");
        }

        [Test]
        public void TryCommitPerk_WithNoPoints_IsRefusedAndChangesNothing()
        {
            _stats.SetSkillLevel("melee", 2);
            Assert.That(_stats.PerkPoints, Is.EqualTo(0), "Precondition: no points to spend");

            bool committed = Source().TryCommitPerk("oak");

            Assert.That(committed, Is.False, "A pick with no point must be refused");
            Assert.That(_stats.GetPerkRank("oak"), Is.EqualTo(0), "…and must not unlock the perk");
        }

        [Test]
        public void TryCommitPerk_WithNoPlayer_IsRefusedRatherThanThrowing()
        {
            var source = new PlayerPipStatSource(() => null, () => null, () => null);

            Assert.That(source.TryCommitPerk("oak"), Is.False,
                "A source with no player must refuse, not throw");
        }

        [Test]
        public void TryCommitPerk_WithNoId_IsRefused()
        {
            _stats.GrantPerkPoints(1);

            Assert.That(Source().TryCommitPerk(null), Is.False, "A null id must be refused");
            Assert.That(Source().TryCommitPerk(string.Empty), Is.False, "An empty id must be refused");
            Assert.That(_stats.PerkPoints, Is.EqualTo(1), "…and no point may be spent");
        }

        [Test]
        public void TryCommitPerk_ChargesWhileTheScriptGrantFormDoesNot()
        {
            // The pair that matters: the same perk, the same zero balance, two forms. `levitation` is
            // the begin land's scripted grant (GameData.as:1225), so its grant form must succeed with no
            // points at all; the picker's form must not.
            Assert.That(_stats.PerkPoints, Is.EqualTo(0), "Precondition: no points");

            Assert.That(Source().TryCommitPerk("levitation"), Is.False,
                "The picker's form charges, so it must refuse at zero points");
            Assert.That(_stats.GetPerkRank("levitation"), Is.EqualTo(0), "…and must not unlock it");

            Assert.That(_stats.AddPerk("levitation"), Is.True,
                "The script's grant form is free and must succeed at zero points");
            Assert.That(_stats.isDJ, Is.EqualTo(1),
                "…and it must set isDJ, the flag the begin land's double jump reads");
        }
    }
}
