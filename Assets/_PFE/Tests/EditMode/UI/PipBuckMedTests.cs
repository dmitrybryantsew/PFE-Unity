using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using PFE.Tests.Editor.Common;
using PFE.UI.PipBuck;

namespace PFE.Tests.EditMode.UI
{
    /// <summary>
    /// Pins the doctor's page as data — <see cref="PipMedList"/>, <see cref="PipMedPrices"/> and
    /// <see cref="PipMedSnapshot"/>. The port of <c>PipPageMed.setSubPages</c>
    /// (<c>PipPageMed.as:63-192</c>) and <c>itemClick</c> (<c>:278-450</c>).
    ///
    /// <para><b>Why the arithmetic is worth a fixture.</b> Every number on this page is a
    /// <i>product</i> of a reading and a coefficient, with two ceilings and one three-term charity
    /// rule on top — and the page charges real caps for the result. A wrong coefficient is not a
    /// cosmetic bug: it is the player paying 4 caps per point of blood instead of 0.5, or a
    /// nearly-destroyed limb costing 600 caps because the <c>raz</c> ceiling was not applied. None of
    /// that is visible in a screenshot, and all of it is one multiplication away from correct.</para>
    ///
    /// <para><b>What is <i>not</i> pinned here.</b> The view (<c>PipPageMedView</c>) and the source
    /// (<c>PlayerPipMedSource</c>): both need a <see cref="UnityEngine.RectTransform"/> or a live
    /// player, so they are exercised by a play-test. What they may not do is decide anything — every
    /// rule is in this file's subject, which is why it can run offline.</para>
    /// </summary>
    [TestFixture]
    public class PipBuckMedTests
    {
        // ── Fixtures ──────────────────────────────────────────────────────────

        /// <summary>A full-health player at Normal difficulty with 100 caps.</summary>
        private static PipMedSnapshot Healthy(int caps = 100)
        {
            return new PipMedSnapshot
            {
                HasPlayer = true,
                Prices = PipMedPrices.Default,
                Level = 10,

                Hp = 100f,
                MaxHp = 100f,

                HeadHp = 200f,
                TorsoHp = 200f,
                LegsHp = 200f,
                BloodHp = 200f,
                ManaOrganHp = 400f,

                InMaxHp = 200f,
                InMaxMana = 400f,

                CurrencyItemId = "money",
                CurrencyBalance = caps,
            };
        }

        /// <summary>
        /// The shipped localisation document, read from disk rather than through
        /// <c>Resources.Load</c> — that is a Unity ECall and throws outside the editor, which is how
        /// the offline harness runs this assembly. Same arrangement, and the same lazy property, as
        /// <c>PipSubTabsTests.Shipped</c>: the offline runner dispatches only SetUp/TearDown, so a
        /// <c>[OneTimeSetUp]</c> would silently never run.
        /// </summary>
        private static PipTextTable _shipped;

        private static PipTextTable Shipped
        {
            get
            {
                if (_shipped != null) return _shipped;

                string path = Path.Combine(SourceLint.ProjectRoot(), "Assets", "_PFE", "Data", "Resources",
                    "Localisation", "text_en.xml");
                Assert.That(File.Exists(path), Is.True, "The localisation document is missing: " + path);

                _shipped = PipTextTable.Parse(File.ReadAllText(path));
                Assert.That(_shipped.IsEmpty, Is.False, "The document parsed to zero entries: " + path);
                return _shipped;
            }
        }

        private static PipMedRow Row(List<PipMedRow> rows, string id)
        {
            foreach (PipMedRow row in rows)
            {
                if (row.Id == id) return row;
            }

            Assert.Fail("No row '" + id + "' in [" +
                        string.Join(", ", rows.ConvertAll(r => r.Id).ToArray()) + "]");
            return default;
        }

        // ── The row set ───────────────────────────────────────────────────────

        /// <summary>
        /// The rows, in the Oracle's own push order (<c>PipPageMed.as:85-192</c>) — health, the
        /// "Body condition" caption, the four physical organs, then mana.
        ///
        /// <para><b>The order is the assertion, not a detail.</b> The caption is pushed <i>between</i>
        /// the health row and the organs so the four organs read as its members, and mana is appended
        /// after blood even though it belongs to a different pool. A sorted or regrouped list would be
        /// tidier and would not be this page.</para>
        /// </summary>
        [Test]
        public void RowsFor_HealthAndFiveOrgans_InTheOraclesOrder()
        {
            List<PipMedRow> rows = PipMedList.RowsFor(Healthy(), PipTextTable.Empty);

            Assert.That(rows.ConvertAll(r => r.Id).ToArray(), Is.EqualTo(new[]
            {
                "hp", "organism", "statHead0", "statTors0", "statLegs0", "statBlood0", "statMana0",
            }), "PipPageMed.as:85-171 pushes these seven, in this order.");
        }

        /// <summary>
        /// The three exposure rows appear <b>only</b> when the source reports exposure readings.
        ///
        /// <para><b>Why this is the honest shape and not a shortcut.</b> The Oracle pushes radiation,
        /// open wounds and poison unconditionally (<c>:172-192</c>) because <c>gg.rad</c>, <c>gg.cut</c>
        /// and <c>gg.poison</c> always exist there. The port keeps none of the three, so printing them
        /// would show three zeroes at a price of zero — a claim about the player that happens to be
        /// true today and would become a lie the moment damage started writing them. An absent row is a
        /// claim about the port, and it is the one that stays true.</para>
        /// </summary>
        [Test]
        public void RowsFor_ExposureRows_AppearOnlyWhenTheSourceHasThem()
        {
            PipMedSnapshot without = Healthy();
            Assert.That(PipMedList.RowsFor(without, PipTextTable.Empty).Exists(r => r.Id == "rad"), Is.False,
                "No exposure readings, so no radiation row.");

            PipMedSnapshot with = Healthy();
            with.HasExposureReadings = true;
            with.Rad = 12f;
            with.Cut = 3.4f;
            with.Poison = 0.25f;

            List<PipMedRow> rows = PipMedList.RowsFor(with, PipTextTable.Empty);
            Assert.That(rows.ConvertAll(r => r.Id).ToArray(), Is.EqualTo(new[]
            {
                "hp", "organism", "statHead0", "statTors0", "statLegs0", "statBlood0", "statMana0",
                "rad", "cut", "poison",
            }), "PipPageMed.as:172-192 appends the three after the organs, in this order.");
        }

        /// <summary>
        /// The Oracle's formatting, row by row — including its one inconsistency: radiation prints an
        /// integer (<c>Math.round</c>, <c>:176</c>) while wounds and poison print one decimal
        /// (<c>Math.round(x * 10) / 10</c>, <c>:183</c> and <c>:190</c>).
        /// </summary>
        [Test]
        public void RowsFor_FormatsEachReadingTheWayTheOracleDoes()
        {
            PipMedSnapshot s = Healthy();
            s.Hp = 143.6f;
            s.MaxHp = 200f;
            s.HeadHp = 98.4f;
            s.InMaxHp = 200f;
            s.ManaOrganHp = 133.3f;
            s.HasExposureReadings = true;
            s.Rad = 12.4f;
            s.Cut = 3.44f;
            s.Poison = 0.25f;

            List<PipMedRow> rows = PipMedList.RowsFor(s, PipTextTable.Empty);

            Assert.That(Row(rows, "hp").Value, Is.EqualTo("144/200"),
                ":88 — Math.round(gg.hp) + \"/\" + Math.round(gg.maxhp).");
            Assert.That(Row(rows, "statHead0").Value, Is.EqualTo("98/200"),
                ":108 — the organ's own reading over inMaxHP.");
            Assert.That(Row(rows, "statMana0").Value, Is.EqualTo("133/400"),
                ":168 — the mana organ's denominator is inMaxMana, not inMaxHP.");
            Assert.That(Row(rows, "rad").Value, Is.EqualTo("12"), ":176 — radiation rounds to an integer.");
            Assert.That(Row(rows, "cut").Value, Is.EqualTo("3.4"), ":183 — wounds print one decimal.");
            Assert.That(Row(rows, "poison").Value, Is.EqualTo("0.3"),
                ":190 — poison prints one decimal, and Math.round(2.5) is 3, not 2.");
        }

        /// <summary>
        /// The section caption carries a label and nothing else — no value, no price, no bar
        /// (<c>PipPageMed.as:92-96</c>). The empty price is what makes the column blank on that row
        /// rather than showing a "0" that a player would read as "free treatment".
        /// </summary>
        [Test]
        public void OrganismHeader_HasNoValueNoPriceAndNoBar()
        {
            PipMedRow row = Row(PipMedList.RowsFor(Healthy(), PipTextTable.Empty), "organism");

            Assert.That(row.Value, Is.Empty);
            Assert.That(row.HasPrice, Is.False);
            Assert.That(row.HasBar, Is.False);
        }

        /// <summary>Only the health pool and the five organs carry a bar; the exposure rows do not.</summary>
        [Test]
        public void OnlyThePoolAndTheOrgansHaveBars()
        {
            PipMedSnapshot s = Healthy();
            s.HasExposureReadings = true;
            s.Rad = 5f;
            s.Cut = 5f;
            s.Poison = 5f;

            List<PipMedRow> rows = PipMedList.RowsFor(s, PipTextTable.Empty);

            Assert.That(Row(rows, "hp").HasBar, Is.True);
            Assert.That(Row(rows, "statHead0").HasBar, Is.True);
            Assert.That(Row(rows, "rad").HasBar, Is.False, "PipPageMed.as:172-178 pushes no `bar` key.");
            Assert.That(Row(rows, "cut").HasBar, Is.False);
            Assert.That(Row(rows, "poison").HasBar, Is.False);
        }

        /// <summary>
        /// The bar is the reading over its own ceiling, clamped — <c>gg.pers.headHP / gg.pers.inMaxHP</c>
        /// feeds a <c>scaleX</c>, which cannot go past 1 or below 0.
        /// </summary>
        [Test]
        public void Bars_AreFractionsOfTheRightCeiling_Clamped()
        {
            PipMedSnapshot s = Healthy();
            s.Hp = 50f;
            s.MaxHp = 200f;
            s.HeadHp = 50f;
            s.TorsoHp = 400f;      // over the ceiling — checkHP() clamps it, and so does the bar
            s.LegsHp = 0f;
            s.ManaOrganHp = 100f;
            s.InMaxHp = 200f;
            s.InMaxMana = 400f;

            List<PipMedRow> rows = PipMedList.RowsFor(s, PipTextTable.Empty);

            Assert.That(Row(rows, "hp").Bar, Is.EqualTo(0.25f).Within(1e-4));
            Assert.That(Row(rows, "statHead0").Bar, Is.EqualTo(0.25f).Within(1e-4));
            Assert.That(Row(rows, "statTors0").Bar, Is.EqualTo(1f).Within(1e-4));
            Assert.That(Row(rows, "statLegs0").Bar, Is.EqualTo(0f).Within(1e-4));
            Assert.That(Row(rows, "statMana0").Bar, Is.EqualTo(0.25f).Within(1e-4),
                "The mana bar divides by inMaxMana (400), not inMaxHP (200).");
        }

        /// <summary>
        /// The organ row ids carry the trauma stage — <c>"statHead" + gg.pers.headSt</c>
        /// (<c>PipPageMed.as:106</c>). The digit is <b>behaviour</b>: it is the key the hover pane looks
        /// its explanation up by, so a stage-2 head explains the stage-2 injury.
        /// </summary>
        [Test]
        public void OrganRowIds_CarryTheTraumaStage()
        {
            PipMedSnapshot s = Healthy();
            s.HeadSt = 2;
            s.TorsoSt = 1;
            s.LegsSt = 3;
            s.BloodSt = 0;
            s.ManaSt = 4;

            List<PipMedRow> rows = PipMedList.RowsFor(s, PipTextTable.Empty);

            Assert.That(Row(rows, "statHead2").Name, Does.Contain("Head"));
            Assert.That(Row(rows, "statTors1").Name, Does.Contain("Body"));
            Assert.That(Row(rows, "statLegs3").Name, Does.Contain("Legs"));
            Assert.That(Row(rows, "statBlood0").Name, Does.Contain("Blood"));
            Assert.That(Row(rows, "statMana4").Name, Does.Contain("Magical"));
        }

        /// <summary>The organ labels are indented by three spaces, under the caption (<c>:107</c>).</summary>
        [Test]
        public void OrganLabels_AreIndentedUnderTheCaption()
        {
            PipMedRow head = Row(PipMedList.RowsFor(Healthy(), PipTextTable.Empty), "statHead0");
            Assert.That(head.Name, Does.StartWith("   "), "PipPageMed.as:107 — `\"   \" + Res.pipText(\"head\")`.");
        }

        // ── Prices ────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>PipMedPrices.Default</c> is the Oracle's own field initialisers
        /// (<c>PipPageMed.as:16-32</c>) and is also the Normal row, because
        /// <c>Pers.setGlobalDif(2)</c> writes exactly these five values.
        /// </summary>
        [Test]
        public void DefaultPrices_AreTheOracleFieldInitialisers()
        {
            PipMedPrices p = PipMedPrices.Default;

            Assert.That(p.Hp, Is.EqualTo(0.5f));
            Assert.That(p.Blood, Is.EqualTo(0.5f));
            Assert.That(p.Rad, Is.EqualTo(1f));
            Assert.That(p.Organ, Is.EqualTo(0.5f));
            Assert.That(p.Cut, Is.EqualTo(4f));
            Assert.That(p.Poison, Is.EqualTo(4f));
            Assert.That(p.Mana, Is.EqualTo(1f));
            Assert.That(p.Raz, Is.EqualTo(200));
            Assert.That(p.RazMana, Is.EqualTo(100));
        }

        /// <summary>
        /// The difficulty table moves <b>five</b> prices and leaves two alone.
        ///
        /// <para><b>The two that do not move are the point.</b> <c>Pers.setGlobalDif</c> writes
        /// <c>priceBlood</c>, <c>priceOrgan</c>, <c>priceRad</c>, <c>pricePoison</c> and
        /// <c>priceCut</c> (<c>Pers.as:769-771, 784-786, 799-801, 814-816, 831-833</c>) and never
        /// touches <c>priceHP</c> (0.5) or <c>priceMana</c> (1). Deriving all seven from the table
        /// would invent a curve the Oracle does not have — and would double the cost of a full heal on
        /// Super-hard, which is exactly the kind of "obvious" symmetry that is wrong.</para>
        /// </summary>
        [Test]
        public void Prices_MoveWithDifficulty_ExceptHealthAndMana()
        {
            PipMedPrices easy = PipMedPrices.ForDifficulty(0);
            PipMedPrices normal = PipMedPrices.ForDifficulty(2);
            PipMedPrices hard = PipMedPrices.ForDifficulty(3);

            Assert.That(normal.Organ, Is.EqualTo(PipMedPrices.Default.Organ),
                "Normal is the identity row: Pers.as:799-801 writes the field initialisers.");
            Assert.That(normal.Blood, Is.EqualTo(PipMedPrices.Default.Blood));
            Assert.That(normal.Rad, Is.EqualTo(PipMedPrices.Default.Rad));
            Assert.That(normal.Cut, Is.EqualTo(PipMedPrices.Default.Cut));

            Assert.That(hard.Organ, Is.EqualTo(1f), "Pers.as:814 — priceOrgan = 1 at difficulty 3.");
            Assert.That(hard.Rad, Is.EqualTo(1.5f), "Pers.as:815 — priceRad = 1.5.");
            Assert.That(hard.Cut, Is.EqualTo(6f), "Pers.as:816 — priceCut = pricePoison = 6.");
            Assert.That(hard.Poison, Is.EqualTo(6f));

            foreach (PipMedPrices p in new[] { easy, normal, hard, PipMedPrices.ForDifficulty(4) })
            {
                Assert.That(p.Hp, Is.EqualTo(0.5f),
                    "priceHP is never written by setGlobalDif — Pers.as:91's initialiser stands.");
                Assert.That(p.Mana, Is.EqualTo(1f),
                    "priceMana is never written by setGlobalDif — Pers.as:97's initialiser stands.");
                Assert.That(p.Raz, Is.EqualTo(200), "raz is a page constant, not a difficulty cell.");
                Assert.That(p.RazMana, Is.EqualTo(100));
            }
        }

        /// <summary>
        /// A full health-pool top-up costs <c>(maxhp - hp) * priceHP</c> — the price the row quotes
        /// (<c>PipPageMed.as:80-90</c>).
        /// </summary>
        [Test]
        public void HealthRowPrice_IsMissingHpTimesTheCoefficient()
        {
            PipMedSnapshot s = Healthy();
            s.Hp = 60f;
            s.MaxHp = 200f;

            PipMedRow hp = Row(PipMedList.RowsFor(s, PipTextTable.Empty), "hp");

            Assert.That(hp.Price, Is.EqualTo(70f).Within(1e-4), "(200 - 60) * 0.5.");
            Assert.That(hp.DisplayPrice, Is.EqualTo(70));
        }

        /// <summary>
        /// A health row that is <i>over</i> its ceiling prices at zero rather than negative — the
        /// Oracle's own guard at <c>PipPageMed.as:81-84</c>.
        ///
        /// <para>Reachable: the unit's pool can sit above <c>maxhp</c> between a level-up's HP grant and
        /// the next clamp. A negative price would render as "-3" in the price column and would <i>pay</i>
        /// the player on a click.</para>
        /// </summary>
        [Test]
        public void HealthRowPrice_IsNeverNegative()
        {
            PipMedSnapshot s = Healthy();
            s.Hp = 250f;
            s.MaxHp = 200f;

            PipMedRow hp = Row(PipMedList.RowsFor(s, PipTextTable.Empty), "hp");
            Assert.That(hp.Price, Is.EqualTo(0f));
        }

        /// <summary>
        /// An organ's price is capped at <c>raz</c> missing HP — <c>if (inMaxHP - headHP &gt; raz)
        /// _loc1_ = raz * priceOrgan;</c> (<c>PipPageMed.as:97-104</c>).
        ///
        /// <para><b>The cap is on HP, not on caps.</b> A limb missing 300 HP does not cost 150 caps; it
        /// costs 100, and the doctor repairs 200 of it. That is the rule that stops a single click from
        /// being an unbounded purchase, and it is the one number a player will notice immediately if it
        /// is wrong.</para>
        /// </summary>
        [Test]
        public void OrganRowPrice_IsCappedAtRazMissingHp()
        {
            PipMedSnapshot s = Healthy();
            s.HeadHp = 0f;        // missing 200 — exactly the cap
            s.TorsoHp = 100f;     // missing 100 — under the cap
            s.LegsHp = -100f;     // missing 300 — over the cap, prices at the cap

            List<PipMedRow> rows = PipMedList.RowsFor(s, PipTextTable.Empty);

            Assert.That(Row(rows, "statHead0").Price, Is.EqualTo(100f).Within(1e-4), "200 * 0.5.");
            Assert.That(Row(rows, "statTors0").Price, Is.EqualTo(50f).Within(1e-4), "100 * 0.5.");
            Assert.That(Row(rows, "statLegs0").Price, Is.EqualTo(100f).Within(1e-4),
                "300 missing is capped to raz = 200, so 100 caps and not 150.");
        }

        /// <summary>
        /// The mana organ has its own, smaller ceiling and its own coefficient — <c>razMana</c> = 100
        /// and <c>priceMana</c> = 1 (<c>PipPageMed.as:157-171</c>).
        /// </summary>
        [Test]
        public void ManaRowPrice_UsesRazManaAndPriceMana()
        {
            PipMedSnapshot s = Healthy();
            s.ManaOrganHp = 0f;
            s.InMaxMana = 400f;

            PipMedRow mana = Row(PipMedList.RowsFor(s, PipTextTable.Empty), "statMana0");

            Assert.That(mana.Price, Is.EqualTo(100f).Within(1e-4),
                "400 missing is capped to razMana = 100, at 1 cap per HP.");
        }

        /// <summary>
        /// Blood is priced with <c>priceBlood</c>, not <c>priceOrgan</c> — they are equal at every
        /// shipped difficulty, which is precisely why a copy-paste of the organ branch into the blood
        /// branch would never be caught by a play-test.
        /// </summary>
        [Test]
        public void BloodRowPrice_UsesTheBloodCoefficient()
        {
            var prices = new PipMedPrices(hp: 0.5f, blood: 2f, rad: 1f, organ: 0.5f, cut: 4f, poison: 4f,
                mana: 1f);

            PipMedSnapshot s = Healthy();
            s.Prices = prices;
            s.BloodHp = 150f;

            List<PipMedRow> rows = PipMedList.RowsFor(s, PipTextTable.Empty);

            Assert.That(Row(rows, "statBlood0").Price, Is.EqualTo(100f).Within(1e-4), "50 missing * 2.");
            Assert.That(Row(rows, "statHead0").Price, Is.EqualTo(0f).Within(1e-4),
                "A full head costs nothing at any coefficient.");
        }

        /// <summary>The header row's price caption is the document's <c>medprice</c> (<c>:74</c>).</summary>
        [Test]
        public void HeaderPrice_IsTheDocumentsMedprice()
        {
            Assert.That(PipMedList.HeaderPrice(Shipped), Is.EqualTo("Healing cost"));
        }

        // ── The click ─────────────────────────────────────────────────────────

        /// <summary>
        /// The clamp — <c>if(_loc2_ &gt; this.plata.kol) _loc2_ = this.plata.kol;</c>
        /// (<c>PipPageMed.as:292-295</c> and every sibling).
        ///
        /// <para><b>The doctor never refuses for want of money.</b> A player with 10 caps and a broken
        /// head pays 10 and gets 20 organ HP. A port that added an affordability check would be a
        /// different game, and it is the obvious "fix" to make on reading this code.</para>
        /// </summary>
        [Test]
        public void Plan_ClampsTheCostToTheBalance()
        {
            PipMedSnapshot s = Healthy(caps: 10);
            s.HeadHp = 0f;                       // would cost 100

            PipMedPlan plan = PipMedList.PlanFor(s, "statHead0");

            Assert.That(plan.Target, Is.EqualTo(PipMedTarget.Organ));
            Assert.That(plan.Cost, Is.EqualTo(10f).Within(1e-4), "Charged everything the player has.");
            Assert.That(plan.Heal, Is.EqualTo(20f).Within(1e-4), "10 caps at 0.5 caps per HP is 20 HP.");
            Assert.That(plan.OrganType, Is.EqualTo(1));
        }

        /// <summary>
        /// The charity branch — <c>PipPageMed.as:384-391</c>: a nearly-destroyed organ, an empty purse
        /// and a player below level 6 buy 49 HP for nothing.
        ///
        /// <para><b>All three terms are required</b>, and the purse term is the <i>pre-payment</i>
        /// balance (<c>:384</c> tests <c>this.plata.kol</c> before <c>:392</c> subtracts). The rule
        /// exists so a broke beginner cannot be permanently crippled by a limb they can neither use nor
        /// afford.</para>
        /// </summary>
        [Test]
        public void Plan_CharityHealsForFree_WhenBrokeLowLevelAndNearlyDestroyed()
        {
            PipMedSnapshot s = Healthy(caps: 0);
            s.Level = 3;
            s.HeadHp = 2f;

            PipMedPlan plan = PipMedList.PlanFor(s, "statHead0");

            Assert.That(plan.IsCharity, Is.True);
            Assert.That(plan.Cost, Is.EqualTo(0f));
            Assert.That(plan.Heal, Is.EqualTo(49f), "The literal 49 at :386, not a computed amount.");
            Assert.That(plan.OrganType, Is.EqualTo(1));
        }

        /// <summary>
        /// Each of the charity rule's three terms, dropped one at a time, must turn it off. A rule that
        /// fired on two of three conditions would hand a free repair to every rich or high-level player
        /// with a scratched limb.
        /// </summary>
        [TestCase(3, 0, 2f, true, TestName = "Charity_AllThreeTerms_Fires")]
        [TestCase(6, 0, 2f, false, TestName = "Charity_LevelSix_DoesNotFire")]
        [TestCase(3, 1, 2f, false, TestName = "Charity_OneCapInThePurse_DoesNotFire")]
        [TestCase(3, 0, 2.5f, false, TestName = "Charity_OrganAboveTwo_DoesNotFire")]
        public void Plan_CharityNeedsAllThreeTerms(int level, int caps, float organHp, bool expectCharity)
        {
            PipMedSnapshot s = Healthy(caps);
            s.Level = level;
            s.HeadHp = organHp;

            PipMedPlan plan = PipMedList.PlanFor(s, "statHead0");
            Assert.That(plan.IsCharity, Is.EqualTo(expectCharity));
        }

        /// <summary>
        /// The charity branch covers <b>the four organs only</b> — head, torso, legs, blood
        /// (<c>:343, :384, :408, :432</c>). The mana organ, the health pool and the three exposure rows
        /// have no such branch, and a copy-paste that gave mana one would make a destroyed mana organ
        /// free to repair at level 5.
        /// </summary>
        [Test]
        public void Plan_CharityDoesNotCoverManaOrThePool()
        {
            PipMedSnapshot s = Healthy(caps: 0);
            s.Level = 3;
            s.ManaOrganHp = 0f;
            s.Hp = 0f;

            Assert.That(PipMedList.PlanFor(s, "statMana0").IsCharity, Is.False,
                "PipPageMed.as:353-369 has no charity branch.");
            Assert.That(PipMedList.PlanFor(s, "hp").IsCharity, Is.False,
                "PipPageMed.as:289-298 has no charity branch.");
        }

        /// <summary>
        /// The prefix dispatch: each organ id selects its own <c>Pers.heal</c> organ argument, and the
        /// four arguments are the ones <c>Pers.as:1863-1918</c> branches on (1 head, 2 torso, 3 legs,
        /// 5 blood, 6 mana).
        ///
        /// <para><b>Note the two numbering schemes.</b> <c>PipPageMed</c>'s <i>unit</i> heals use 0 for
        /// the pool, 2 for radiation, 3 for wounds and 4 for poison (<c>UnitPlayer.as:3155-3201</c>),
        /// while its <i>organ</i> heals use 1/2/3/5/6. The same digits mean different organs on the two
        /// sides, which is exactly the trap a "unify the numbering" refactor would fall into.</para>
        /// </summary>
        [TestCase("statHead1", 1, TestName = "Dispatch_Head_IsOrganOne")]
        [TestCase("statTors2", 2, TestName = "Dispatch_Torso_IsOrganTwo")]
        [TestCase("statLegs3", 3, TestName = "Dispatch_Legs_IsOrganThree")]
        [TestCase("statBlood0", 5, TestName = "Dispatch_Blood_IsOrganFive")]
        [TestCase("statMana0", 6, TestName = "Dispatch_Mana_IsOrganSix")]
        public void Plan_DispatchesEachOrganPrefixToItsOwnHealType(string rowId, int expectedOrganType)
        {
            PipMedPlan plan = PipMedList.PlanFor(Healthy(), rowId);

            Assert.That(plan.Acts, Is.True);
            Assert.That(plan.OrganType, Is.EqualTo(expectedOrganType));
        }

        /// <summary>
        /// The section header's click does nothing — the Oracle's ladder has no branch for
        /// <c>organism</c>, so it falls through to the sound and the repaint (<c>:446-449</c>).
        /// </summary>
        [Test]
        public void Plan_OnTheSectionHeader_DoesNothing()
        {
            PipMedPlan plan = PipMedList.PlanFor(Healthy(), "organism");

            Assert.That(plan.Acts, Is.False);
            Assert.That(plan.Heal, Is.EqualTo(0f));
            Assert.That(plan.Cost, Is.EqualTo(0f));
        }

        /// <summary>
        /// The three exposure rows heal through the <b>unit</b>'s numbering, and only when they exist.
        /// </summary>
        [TestCase("rad", 2, TestName = "Dispatch_Radiation_IsUnitTypeTwo")]
        [TestCase("cut", 3, TestName = "Dispatch_Wounds_IsUnitTypeThree")]
        [TestCase("poison", 4, TestName = "Dispatch_Poison_IsUnitTypeFour")]
        public void Plan_DispatchesExposureRowsToTheUnitHealTypes(string rowId, int expectedOrganType)
        {
            PipMedSnapshot s = Healthy();
            s.HasExposureReadings = true;
            s.Rad = 4f;
            s.Cut = 2f;
            s.Poison = 1f;

            PipMedPlan plan = PipMedList.PlanFor(s, rowId);

            Assert.That(plan.Acts, Is.True);
            Assert.That(plan.OrganType, Is.EqualTo(expectedOrganType));
        }

        /// <summary>
        /// <c>pip.noAct</c> refuses every click before the ladder runs (<c>PipPageMed.as:282-286</c>) —
        /// so a treatment during a cutscene costs nothing and heals nothing.
        /// </summary>
        [Test]
        public void Plan_IsRefusedWhenActionsAreBlocked()
        {
            PipMedSnapshot s = Healthy();
            s.ActionsBlocked = true;
            s.HeadHp = 0f;

            Assert.That(PipMedList.PlanFor(s, "statHead0").Acts, Is.False);
            Assert.That(PipMedList.PlanFor(s, "hp").Acts, Is.False);
        }

        /// <summary>
        /// The heal amount is the <b>unrounded</b> cost over the price while the charge is the rounded
        /// one — the Oracle's own asymmetry (<c>:296-297</c>: <c>heal(_loc2_ / price)</c> then
        /// <c>plata.kol -= Math.round(_loc2_)</c>). A 1.4-cap treatment heals 2.8 HP and costs 1 cap.
        /// </summary>
        [Test]
        public void Plan_HealsTheUnroundedAmount_AndChargesTheRoundedOne()
        {
            PipMedSnapshot s = Healthy(caps: 100);
            s.HeadHp = 197.2f;      // 2.8 missing

            PipMedPlan plan = PipMedList.PlanFor(s, "statHead0");

            Assert.That(plan.Cost, Is.EqualTo(1.4f).Within(1e-3f));
            Assert.That(plan.Heal, Is.EqualTo(2.8f).Within(1e-3f));
            Assert.That(PipBuckReadoutFormat.As3Round(plan.Cost), Is.EqualTo(1),
                "Math.round(1.4) is 1 — the charge, not the heal.");
        }

        /// <summary>
        /// Only a caps-paying doctor's takings go to its own wallet — <c>this.plata.id == "money"</c>
        /// (<c>PipPageMed.as:442</c>). An Auto-Doc's gel is a consumable, not money.
        /// </summary>
        [TestCase("money", true, TestName = "Purse_Caps_AreMoney")]
        [TestCase("gel", false, TestName = "Purse_Gel_IsNotMoney")]
        [TestCase("good", false, TestName = "Purse_Goods_AreNotMoney")]
        public void Plan_SpendsMoneyOnlyForTheCapsPurse(string currency, bool expectMoney)
        {
            PipMedSnapshot s = Healthy();
            s.CurrencyItemId = currency;
            s.HeadHp = 0f;

            Assert.That(PipMedList.PlanFor(s, "statHead0").SpendsMoney, Is.EqualTo(expectMoney));
        }

        // ── The purse and the footer ──────────────────────────────────────────

        /// <summary>
        /// <c>PipPageMed.as:48-62</c>: <c>adoc</c> is paid in gel, <c>vdoc</c> in goods, everything else
        /// in caps.
        /// </summary>
        [TestCase("doc", "money", TestName = "Purse_Doc_TakesCaps")]
        [TestCase("adoc", "gel", TestName = "Purse_Adoc_TakesGel")]
        [TestCase("vdoc", "good", TestName = "Purse_Vdoc_TakesGoods")]
        [TestCase("", "money", TestName = "Purse_NoInter_TakesCaps")]
        [TestCase("v", "money", TestName = "Purse_AVendingMachine_TakesCaps")]
        public void CurrencyFor_PicksThePurseByInteractionKind(string npcInter, string expected)
        {
            Assert.That(PlayerPipMedSource.CurrencyFor(npcInter), Is.EqualTo(expected));
        }

        /// <summary>
        /// The footer names the purse — and caps come from the <c>pip</c> table while gel and goods come
        /// from the <c>item</c> table (<c>PipPageMed.as:262-276</c>). Reading all three from <c>item</c>
        /// would print "Bag with caps", which is the inventory's name for the stack and not what a
        /// doctor says.
        /// </summary>
        [TestCase("money", "Caps", TestName = "Footer_Caps_UsesThePipTable")]
        [TestCase("gel", "Biogel", TestName = "Footer_Gel_UsesTheItemTable")]
        [TestCase("good", "Kindness", TestName = "Footer_Goods_UseTheItemTable")]
        public void BottomText_NamesThePurseFromTheOraclesTable(string currency, string expectedLabel)
        {
            PipMedSnapshot s = Healthy(caps: 42);
            s.CurrencyItemId = currency;

            string text = PipMedList.BottomText(s, Shipped);

            Assert.That(text, Does.StartWith(expectedLabel + ": "));
            Assert.That(text, Does.Contain("42"));
        }

        // ── The localisation document ─────────────────────────────────────────

        /// <summary>
        /// Every caption the page can print exists in the shipped document — the data-side check that
        /// mirrors the code's own list, so a rule error cannot pass unnoticed.
        /// </summary>
        [TestCase("hp")]
        [TestCase("organism")]
        [TestCase("head")]
        [TestCase("tors")]
        [TestCase("legs")]
        [TestCase("blood")]
        [TestCase("mana")]
        [TestCase("rad")]
        [TestCase("cut")]
        [TestCase("poison")]
        [TestCase("caps")]
        [TestCase("medprice")]
        [TestCase("med1")]
        [TestCase("med2")]
        public void EveryCaptionThisPagePrintsIsInTheDocument(string key)
        {
            Assert.That(Shipped.TryGet("p", key, 0, out string text), Is.True,
                "The document has no `pip` entry '" + key + "'.");
            Assert.That(text, Is.Not.Empty);
        }

        /// <summary>
        /// <c>hp</c> resolves to <b>"Health"</b>, not "HP".
        ///
        /// <para><b>A duplicate id, and the Oracle's tie-break.</b> The document carries two
        /// <c>&lt;pip id="hp"&gt;</c> rows: "Health" with an info paragraph, and later "HP" without one.
        /// <c>Res.txt</c> takes <c>xl[0]</c> (<c>Res.as:70-71</c>) — the <i>first</i> match — while the
        /// port's table kept the last, so the character sheet's Health sub-page and this page's health
        /// row both quoted a word the Oracle does not show there. Exactly one id in the whole document
        /// is duplicated, so the fix's blast radius is this one word.</para>
        /// </summary>
        [Test]
        public void HealthRowLabel_IsHealth_NotTheDocumentsLaterDuplicate()
        {
            Assert.That(Shipped.TryGet("p", "hp", 0, out string text), Is.True);
            Assert.That(text, Is.EqualTo("Health"),
                "Res.txt takes the FIRST match (Res.as:70-71); the document's second `hp` row says \"HP\".");
        }

        /// <summary>
        /// The row explanations are looked up by the <b>row id</b>, and the ids include the trauma stage
        /// — so a stage-2 head explains a stage-2 injury. Every stage the Oracle can produce for the
        /// four physical organs has an entry.
        /// </summary>
        [TestCase("statHead0")]
        [TestCase("statHead1")]
        [TestCase("statHead2")]
        [TestCase("statHead3")]
        [TestCase("statTors0")]
        [TestCase("statLegs3")]
        [TestCase("statBlood0")]
        [TestCase("statMana0")]
        public void EveryTraumaStageRowHasAnExplanation(string rowId)
        {
            Assert.That(Shipped.TryGetInTable("pip", rowId, 1, out string info), Is.True,
                "No `pip` entry '" + rowId + "' with an <info> paragraph — the hover pane would be blank.");
            Assert.That(info, Is.Not.Empty);
        }
    }
}
