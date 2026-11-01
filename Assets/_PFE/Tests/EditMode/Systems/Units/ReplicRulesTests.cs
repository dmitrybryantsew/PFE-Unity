using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Sim.Units;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.EditMode.Systems.Units
{
    /// <summary>
    /// Pins enemy speech against the shipped localisation document and the oracle's literals —
    /// <c>Unit.replic</c> (<c>Unit.as:4813-4850</c>), the <c>UnitRaider</c> override
    /// (<c>UnitRaider.as:1467-1479</c>) and <c>Res.repText</c> (<c>Res.as:281-312</c>).
    ///
    /// <para><b>What was missing before this fixture.</b> The port had the <c>&lt;replic&gt;</c> table
    /// in its own Resources the whole time — 102 rows, 982 lines — and nothing read it: every sapient
    /// enemy in the game was mute, and the two comments that named the gap ("the port has no
    /// per-unit voice-line table") described a missing subsystem rather than a missing call. A
    /// subsystem that exists but is never exercised is exactly what a fixture has to hold down,
    /// because the failure mode is silence, and silence looks like "nothing to port".</para>
    ///
    /// <para><b>The numbers are measured, not remembered.</b> 102/982/17 are counts over
    /// <c>text_en.xml</c> as shipped; if the document is re-exported with more lines the first test
    /// fails and says so, which is the intended behaviour — a speech table that silently loses half its
    /// lines is indistinguishable from enemies that have nothing to say.</para>
    /// </summary>
    public class ReplicRulesTests
    {
        private static ReplicTextTable _shipped;

        /// <summary>The shipped document, parsed once.</summary>
        private static ReplicTextTable Shipped
        {
            get
            {
                if (_shipped != null) return _shipped;

                string path = Path.Combine(
                    SourceLint.ProjectRoot(), "Assets", "_PFE", "Data", "Resources", "Localisation",
                    "text_en.xml");

                Assert.That(File.Exists(path), Is.True, "The localisation document is missing: " + path);

                _shipped = ReplicTextTable.Parse(File.ReadAllText(path));
                return _shipped;
            }
        }

        // ── The document ─────────────────────────────────────────────────────

        /// <summary>
        /// The block is parsed whole: 102 <c>&lt;rep&gt;</c> rows, 982 <c>&lt;r&gt;</c> lines and the 17
        /// speaker ids the file authors.
        /// </summary>
        [Test]
        public void ShippedDocument_ParsesEveryRowAndLine()
        {
            Assert.That(Shipped.RowCount, Is.EqualTo(102), "text_en.xml's <replic> block");
            Assert.That(Shipped.LineCount, Is.EqualTo(982), "every <r> in the block");

            var speakers = new System.Collections.Generic.List<string>(Shipped.Speakers);
            Assert.That(speakers.Count, Is.EqualTo(17), "distinct <rep id> values");
            CollectionAssert.Contains(speakers, "raider");
            CollectionAssert.Contains(speakers, "ranger");
            CollectionAssert.Contains(speakers, "robobrain");
            CollectionAssert.Contains(speakers, "protect");
            CollectionAssert.Contains(speakers, "gutsy");
            CollectionAssert.Contains(speakers, "sentinel");
            CollectionAssert.Contains(speakers, "eqd");
            CollectionAssert.Contains(speakers, "pip");
        }

        /// <summary>
        /// The three enemy families the feature was asked for each have an <c>attack</c> line, and the
        /// counts are the document's own: a raider has 26 ways to say "I see you", a steel ranger 15.
        /// </summary>
        [Test]
        public void TheThreeSapientFamilies_SpeakOnSight()
        {
            Assert.That(Shipped.TryGetLines("raider", "attack", out var raider), Is.True);
            Assert.That(raider.Count, Is.EqualTo(26));

            Assert.That(Shipped.TryGetLines("ranger", "attack", out var ranger), Is.True);
            Assert.That(ranger.Count, Is.EqualTo(15));

            Assert.That(Shipped.TryGetLines("robobrain", "attack", out var robot), Is.True);
            Assert.That(robot.Count, Is.GreaterThan(0));
        }

        /// <summary>
        /// An act the speaker does not have is <b>not</b> an error — <c>Res.repText</c> returns
        /// <c>""</c> and <c>replic</c> emits nothing. The raider has no <c>hi</c>; a greeting is
        /// something ponies do.
        /// </summary>
        [Test]
        public void AMissingAct_IsSilentRatherThanAnError()
        {
            Assert.That(Shipped.TryGetLines("raider", "hi", out _), Is.False);
            Assert.That(Shipped.HasSpeaker("raider"), Is.True);
            Assert.That(Shipped.HasSpeaker("nosuchsperker"), Is.False);

            Assert.That(
                ReplicRules.TryResolveText(Shipped, "raider", ReplicKind.Hi, 0f, true, false, null, out string text),
                Is.False);
            Assert.That(text, Is.Null);
        }

        // ── The cooldown ─────────────────────────────────────────────────────

        /// <summary>
        /// <c>attack</c> costs 50…149 ticks, everything else 110…259 — <c>50 + random*100</c> and
        /// <c>110 + random*150</c> assigned to an <c>int</c>, which truncates.
        /// </summary>
        [Test]
        public void Cooldowns_MatchTheOracleLiterals()
        {
            Assert.That(ReplicRules.CooldownFor(ReplicKind.Attack, 0f), Is.EqualTo(50));
            Assert.That(ReplicRules.CooldownFor(ReplicKind.Attack, 0.999f), Is.EqualTo(149));

            Assert.That(ReplicRules.CooldownFor(ReplicKind.Neutral, 0f), Is.EqualTo(110));
            Assert.That(ReplicRules.CooldownFor(ReplicKind.Neutral, 0.999f), Is.EqualTo(259));
            Assert.That(ReplicRules.CooldownFor(ReplicKind.Dam, 0.999f), Is.EqualTo(259));
        }

        /// <summary>
        /// The initial value is <c>Math.random() * 100 - 50</c> truncated to <c>[-50, 49]</c> — negative
        /// on purpose, so a fresh unit is <i>ready</i> to speak rather than muted for its first seconds.
        /// </summary>
        [Test]
        public void InitialCooldown_IsMinusFiftyToFortyNine()
        {
            Assert.That(ReplicRules.InitialCooldown(0f), Is.EqualTo(-50));
            Assert.That(ReplicRules.InitialCooldown(0.999f), Is.EqualTo(49));
        }

        /// <summary>
        /// A live cooldown blocks the bark and is left alone; an expired one is replaced by the new
        /// value. This is the gate that stops a burst of hits printing a line per bullet.
        /// </summary>
        [Test]
        public void AnOpenWindow_IsRequired_AndChargesTheNewCooldown()
        {
            int cooldown = 40;
            Assert.That(ReplicRules.TryOpenWindow(ReplicKind.Attack, ref cooldown, 0f, 0f), Is.False);
            Assert.That(cooldown, Is.EqualTo(40), "a refused bark must not move the clock");

            cooldown = 0;
            Assert.That(ReplicRules.TryOpenWindow(ReplicKind.Attack, ref cooldown, 0f, 0f), Is.True);
            Assert.That(cooldown, Is.EqualTo(50), "attack's base");

            cooldown = -12;
            Assert.That(ReplicRules.TryOpenWindow(ReplicKind.Neutral, ref cooldown, 0f, 0f), Is.True);
            Assert.That(cooldown, Is.EqualTo(110), "the other arm's base");
        }

        /// <summary>
        /// <c>dam</c> opens the window on 5 % of calls and <c>die</c> on 50 % — and both only by zeroing
        /// the cooldown, which is what the subsequent gate then reads.
        /// </summary>
        [Test]
        public void DamAndDie_HaveTheirOwnWindowChances()
        {
            int cooldown = 200;

            Assert.That(ReplicRules.TryOpenWindow(ReplicKind.Dam, ref cooldown, 0.04f, 0f), Is.True,
                "a 0.04 draw is inside the 5 % window");
            Assert.That(cooldown, Is.EqualTo(110));

            cooldown = 200;
            Assert.That(ReplicRules.TryOpenWindow(ReplicKind.Dam, ref cooldown, 0.06f, 0f), Is.False,
                "a 0.06 draw is outside it, so the 200-tick cooldown stands");
            Assert.That(cooldown, Is.EqualTo(200));

            cooldown = 200;
            Assert.That(ReplicRules.TryOpenWindow(ReplicKind.Die, ref cooldown, 0.49f, 0f), Is.True);
            cooldown = 200;
            Assert.That(ReplicRules.TryOpenWindow(ReplicKind.Die, ref cooldown, 0.51f, 0f), Is.False);
        }

        /// <summary>
        /// The cooldown is charged <b>before</b> the text is resolved, so an act the voice set lacks
        /// still costs a full cooldown. Without this an enemy whose table has no <c>vse</c> would retry
        /// on every frame of the state that calls it.
        /// </summary>
        [Test]
        public void AMissingLine_StillCostsTheCooldown()
        {
            int cooldown = 0;
            Assert.That(ReplicRules.TryOpenWindow(ReplicKind.Vse, ref cooldown, 0f, 0f), Is.True);
            Assert.That(cooldown, Is.EqualTo(110), "charged even though the speaker may have no vse row");

            Assert.That(
                ReplicRules.TryResolveText(Shipped, "raider", ReplicKind.Vse, 0f, true, false, null, out _),
                Is.True, "a raider does have one — the point is the order, not this speaker");
        }

        // ── The raider variant override ──────────────────────────────────────

        /// <summary>
        /// <c>raider8</c> is the flamer body: 30 % of its attack barks become <c>fire</c> lines.
        /// <c>raider9</c> is the grenadier and swaps to <c>expl</c>.
        /// </summary>
        [Test]
        public void FlamerAndGrenadier_SwapTheirAttackLineOnThirtyPercent()
        {
            Assert.That(ReplicRules.OverrideRaiderKind(ReplicKind.Attack, 8, 0.29f), Is.EqualTo(ReplicKind.Fire));
            Assert.That(ReplicRules.OverrideRaiderKind(ReplicKind.Attack, 8, 0.31f), Is.EqualTo(ReplicKind.Attack));
            Assert.That(ReplicRules.OverrideRaiderKind(ReplicKind.Attack, 9, 0.29f), Is.EqualTo(ReplicKind.Expl));
            Assert.That(ReplicRules.OverrideRaiderKind(ReplicKind.Attack, 9, 0.31f), Is.EqualTo(ReplicKind.Attack));
        }

        /// <summary>
        /// Every other body keeps its generic line, and no other act is ever swapped — the oracle's two
        /// <c>if</c>s are both guarded by <c>param1 == "attack"</c>.
        /// </summary>
        [Test]
        public void OtherBodiesAndOtherActs_AreNeverSwapped()
        {
            Assert.That(ReplicRules.OverrideRaiderKind(ReplicKind.Attack, 1, 0f), Is.EqualTo(ReplicKind.Attack));
            Assert.That(ReplicRules.OverrideRaiderKind(ReplicKind.Attack, 7, 0f), Is.EqualTo(ReplicKind.Attack));
            Assert.That(ReplicRules.OverrideRaiderKind(ReplicKind.Dam, 8, 0f), Is.EqualTo(ReplicKind.Dam));
            Assert.That(ReplicRules.OverrideRaiderKind(ReplicKind.Neutral, 9, 0f), Is.EqualTo(ReplicKind.Neutral));
        }

        /// <summary>
        /// The two variant lines really exist in the document — an override that names an act nobody
        /// wrote would be a silent no-op that still charges a cooldown.
        /// </summary>
        [Test]
        public void TheVariantActs_ExistInTheDocument()
        {
            Assert.That(Shipped.TryGetLines("raider", "fire", out var fire), Is.True);
            Assert.That(fire.Count, Is.EqualTo(8));
            Assert.That(Shipped.TryGetLines("raider", "expl", out var expl), Is.True);
            Assert.That(expl.Count, Is.EqualTo(8));
        }

        // ── Line selection and substitution ──────────────────────────────────

        /// <summary>
        /// The index is <c>floor(draw * count)</c>, and the draw is the caller's. <c>raider</c>'s 26
        /// attack lines mean a 0.75 draw lands on index 19 — the mature-only "Say goodbye, cunt!".
        /// </summary>
        [Test]
        public void TheDrawSelectsTheLine_AndAnUnfilteredBuildSeesIt()
        {
            Assert.That(
                ReplicRules.TryResolveText(Shipped, "raider", ReplicKind.Attack, 0.75f, true, false, null, out string text),
                Is.True);
            Assert.That(text, Is.EqualTo("Say goodbye, cunt!"));
        }

        /// <summary>
        /// The same draw with the content filter on produces <b>nothing at all</b> — <c>m="1"</c> lines
        /// are dropped, not cleaned (<c>Res.as:299-302</c>). This is the opposite of what the same
        /// attribute means in the <c>&lt;txt&gt;</c> tables, where it selects a half of a
        /// <c>mature|clean</c> pair.
        /// </summary>
        [Test]
        public void AMatureOnlyLine_IsDroppedWhenTheFilterIsOn()
        {
            Assert.That(
                ReplicRules.TryResolveText(Shipped, "raider", ReplicKind.Attack, 0.75f, true, true, null, out string text),
                Is.False);
            Assert.That(text, Is.Null);
        }

        /// <summary>
        /// A draw that lands on a clean line is unaffected by the filter — the gate is per line, not per
        /// speaker.
        /// </summary>
        [Test]
        public void ACleanLine_SurvivesTheFilter()
        {
            Assert.That(
                ReplicRules.TryResolveText(Shipped, "raider", ReplicKind.Attack, 0f, true, true, null, out string text),
                Is.True);
            Assert.That(text, Is.EqualTo("Prepare to die!"));
        }

        /// <summary>
        /// The mature flag is the <b>attribute's presence</b>, not its value —
        /// <c>Boolean(xl[num].@m.length())</c> (<c>Res.as:299</c>). Every shipped row writes
        /// <c>m="1"</c> (41 of them, measured), so a <c>m="0"</c> row is the only case that tells the two
        /// rules apart — and there is none in the data, which is exactly why this is pinned on a
        /// synthetic row instead of left to a reader's memory.
        /// </summary>
        [Test]
        public void MatureFlag_IsPresenceNotValue()
        {
            var table = ReplicTextTable.Parse(
                "<replic><rep act=\"attack\" id=\"t\"><r m=\"0\">zero is still mature</r></rep></replic>");

            Assert.That(
                ReplicRules.TryResolveText(table, "t", ReplicKind.Attack, 0f, true, true, null, out string text),
                Is.False, "m present with a value of 0 is mature in the oracle");
            Assert.That(text, Is.Null);
        }

        /// <summary>
        /// <c>@lp</c> becomes the player's name — <c>s.replace("@lp", World.w.pers.persName)</c>
        /// (<c>Res.as:310</c>). <c>child</c>/<c>hi</c> index 4 is "Hi, @lp".
        /// </summary>
        [Test]
        public void PlayerName_IsSubstituted()
        {
            Assert.That(
                ReplicRules.TryResolveText(Shipped, "child", ReplicKind.Hi, 0.5f, true, false, "Littlepip",
                    out string text),
                Is.True);
            Assert.That(text, Is.EqualTo("Hi, Littlepip"));

            // No name supplied leaves the token — a deliberate deviation: AS3 would substitute the
            // literal "undefined". See ReplicRules.TryResolveText's remarks.
            Assert.That(
                ReplicRules.TryResolveText(Shipped, "child", ReplicKind.Hi, 0.5f, true, false, null, out string raw),
                Is.True);
            Assert.That(raw, Is.EqualTo("Hi, @lp"));
        }

        /// <summary>
        /// <b>Only the first <c>@lp</c> is replaced.</b> AS3's <c>String.replace</c> with a string pattern
        /// is not global (<c>Res.as:310</c>), and ten shipped lines carry the token twice — so a port that
        /// used .NET's global <c>String.Replace</c> would substitute where the oracle leaves the second
        /// token standing. That was the port's own bug until this test, which is why it is pinned on a
        /// synthetic row whose shape is unmistakable.
        /// </summary>
        [Test]
        public void PlayerName_ReplacesOnlyTheFirstToken()
        {
            var table = ReplicTextTable.Parse(
                "<replic><rep act=\"hi\" id=\"t\"><r>Hi, @lp from the future - says @lp</r></rep></replic>");

            Assert.That(
                ReplicRules.TryResolveText(table, "t", ReplicKind.Hi, 0f, true, false, "Littlepip", out string text),
                Is.True);
            Assert.That(text, Is.EqualTo("Hi, Littlepip from the future - says @lp"));
        }

        /// <summary>
        /// The <c>#male|female#</c> splice takes half 0 for <c>msex</c> and half 1 otherwise
        /// (<c>Res.as:303-309</c>). No shipped line uses it — measured 0 of 982 — so this is pinned on a
        /// synthetic row rather than dropped for being unexercised.
        /// </summary>
        [Test]
        public void GenderSplice_TakesTheMaleOrFemaleHalf()
        {
            var table = ReplicTextTable.Parse(
                "<replic><rep act=\"dam\" id=\"t\"><r>#Ouch, sir!|Ouch, ma'am!#</r></rep></replic>");

            Assert.That(ReplicRules.TryResolveText(table, "t", ReplicKind.Dam, 0f, true, false, null, out string male), Is.True);
            Assert.That(male, Is.EqualTo("Ouch, sir!"));

            Assert.That(ReplicRules.TryResolveText(table, "t", ReplicKind.Dam, 0f, false, false, null, out string female), Is.True);
            Assert.That(female, Is.EqualTo("Ouch, ma'am!"));
        }

        /// <summary>
        /// A one-part splice falls back to that part instead of producing "undefined" — AS3 would
        /// concatenate <c>undefined</c> here, which is a bug the data never exercises.
        /// </summary>
        [Test]
        public void AOnePartSplice_DoesNotProduceUndefined()
        {
            var table = ReplicTextTable.Parse(
                "<replic><rep act=\"dam\" id=\"t\"><r>#only one half#</r></rep></replic>");

            Assert.That(ReplicRules.TryResolveText(table, "t", ReplicKind.Dam, 0f, false, false, null, out string text), Is.True);
            Assert.That(text, Is.EqualTo("only one half"));
        }

        /// <summary>
        /// <c>KeyFor</c> and <c>KindFor</c> are inverses, and the one misspelled act stays misspelled —
        /// <c>donttoch</c> is the document's spelling.
        /// </summary>
        [Test]
        public void ActKeys_RoundTrip()
        {
            foreach (ReplicKind kind in System.Enum.GetValues(typeof(ReplicKind)))
            {
                if (kind == ReplicKind.None) continue;
                string key = ReplicRules.KeyFor(kind);
                Assert.That(key, Is.Not.Null.And.Not.Empty, kind.ToString());
                Assert.That(ReplicRules.KindFor(key), Is.EqualTo(kind), key);
            }

            Assert.That(ReplicRules.KeyFor(ReplicKind.DontToch), Is.EqualTo("donttoch"));
            Assert.That(ReplicRules.KindFor("not-an-act"), Is.EqualTo(ReplicKind.None));
        }

        // ── The unit id → voice set map ──────────────────────────────────────

        private static XElement ShippedUnitRoot()
        {
            string path = Path.Combine(
                SourceLint.ProjectRoot(), "Assets", "_PFE", "Data", "Resources", "Loot", "AllData.txt");

            Assert.That(File.Exists(path), Is.True, "The AllData mirror is missing: " + path);

            AllDataXml.Outcome outcome =
                AllDataXml.TryRead(File.ReadAllText(path), out XElement root, out _, out string error);

            Assert.That(outcome, Is.EqualTo(AllDataXml.Outcome.Ok), error);
            return root;
        }

        /// <summary>
        /// Only the family rows carry <c>&lt;vis replic='…'&gt;</c>; the nine <c>raiderN</c> variants are
        /// separate rows with none, and the oracle bridges the gap by calling
        /// <c>super.getXmlParam(parentId)</c> before <c>super.getXmlParam()</c>
        /// (<c>UnitRaider.as:296-298</c>). Measured on the shipped document: 148 unit rows, 18 naming a
        /// voice set directly, 57 with a <c>@parent</c>.
        ///
        /// <para><b>This is the test that decides whether nine tenths of the raiders are mute.</b> A
        /// lookup reading only the unit's own row passes every other test in this file and leaves
        /// <c>raider1</c>…<c>raider9</c> silent, which is indistinguishable from "the port has no speech
        /// yet".</para>
        /// </summary>
        [Test]
        public void ShippedUnitRows_ResolveEveryVariantToItsFamilyVoiceSet()
        {
            ReplicSpeakerMap map = ReplicSpeakerMap.FromUnitRoot(ShippedUnitRoot());

            Assert.That(map.DirectCount, Is.EqualTo(18), "unit rows naming <vis replic>");

            Assert.That(map.SpeakerFor("raider"), Is.EqualTo("raider"));
            for (int variant = 1; variant <= 9; variant++)
                Assert.That(map.SpeakerFor("raider" + variant), Is.EqualTo("raider"), "raider" + variant);

            Assert.That(map.SpeakerFor("ranger"), Is.EqualTo("ranger"));
            Assert.That(map.SpeakerFor("robobrain"), Is.EqualTo("robobrain"));

            // Inheriting through a family is not raider-only: a slaver speaks as a raider and both
            // drone bodies speak as a sentinel.
            Assert.That(map.SpeakerFor("slaver"), Is.EqualTo("raider"));
            Assert.That(map.SpeakerFor("bossultra"), Is.EqualTo("sentinel"));
            Assert.That(map.SpeakerFor("bossdron"), Is.EqualTo("sentinel"));
            Assert.That(map.SpeakerFor("protect1"), Is.EqualTo("protect"));

            // The absent control: a unit whose whole ancestor chain names nothing is silent, and stays
            // silent on a second ask (the miss is cached, and a cached miss must not become a hit).
            Assert.That(map.SpeakerFor("training"), Is.Null);
            Assert.That(map.SpeakerFor("training"), Is.Null);
            Assert.That(map.SpeakerFor("no-such-unit"), Is.Null);
            Assert.That(map.SpeakerFor(null), Is.Null);
            Assert.That(map.SpeakerFor(""), Is.Null);
        }

        /// <summary>
        /// The join between the two documents: every voice set <c>AllData</c> names must be a speaker the
        /// text table authors. 13 distinct ids, all present. A mismatch here is the failure mode that
        /// looks like "this enemy has nothing to say" rather than like a data error.
        ///
        /// <para>Four speakers in the table have no unit row naming them — <c>child</c>, <c>sortir</c>,
        /// <c>stabpon</c> and <c>pip</c>. <c>pip</c> is the player and is reached by id, not by the map;
        /// the other three are lines no unit row currently addresses, so they are unreachable through
        /// this path in the oracle too. Recorded, not "fixed" — inventing a mapping would be a port of
        /// something that does not exist.</para>
        /// </summary>
        [Test]
        public void EveryVoiceSetTheUnitRowsName_ExistsInTheTextDocument()
        {
            var named = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (XElement vis in ShippedUnitRoot().Descendants("vis"))
            {
                string replic = (string)vis.Attribute("replic");
                if (!string.IsNullOrEmpty(replic)) named.Add(replic);
            }

            Assert.That(named.Count, Is.EqualTo(13), "distinct <vis replic> values");

            foreach (string speaker in named)
                Assert.That(Shipped.HasSpeaker(speaker), Is.True, "no <rep id='" + speaker + "'> in text_en.xml");

            // The three orphan speakers, pinned so the note above cannot rot silently.
            Assert.That(Shipped.HasSpeaker("sortir"), Is.True);
            Assert.That(Shipped.HasSpeaker("stabpon"), Is.True);
            Assert.That(named.Contains("sortir"), Is.False);
            Assert.That(named.Contains("stabpon"), Is.False);
        }
    }
}
