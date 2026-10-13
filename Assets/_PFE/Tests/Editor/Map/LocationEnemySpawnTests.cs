using System;
using System.Collections.Generic;
using NUnit.Framework;
using PFE.Core.Rng;
using PFE.Systems.Map;

namespace PFE.Tests.Editor.Map
{
    /// <summary>
    /// A scripted <see cref="IRngService"/> for the location-spawn fixtures.
    ///
    /// <para><b>Why a script rather than a seed.</b> Every function under test here is a port of an AS3
    /// function whose <i>draw count</i> is part of its contract — the spawn stream is shared with room
    /// layout, so a branch that takes one draw too few re-rolls every later placement in the land with no
    /// error and no warning. A seeded stream can only show that two runs differ; a script lets a test
    /// state "the third draw decides this" and then assert the count as well as the value.</para>
    ///
    /// <para><see cref="Range(int,int)"/> is derived from the same float script
    /// (<c>min + floor(v * span)</c>), which is exactly AS3's
    /// <c>param2 + Math.floor(Math.random() * (param3 - param2 + 1))</c>, so one script drives both
    /// <c>NextFloat()</c> and the count rolls.</para>
    /// </summary>
    internal sealed class LocationScriptedRng : IRngService
    {
        private readonly Queue<float> _script;
        private readonly float _repeatAfterScript;

        /// <param name="repeatAfterScript">
        /// The value returned once the script is exhausted.
        /// <b>This is not the first draw.</b> Prefer the <see cref="Always"/> and <see cref="Sequence"/>
        /// factories below, which say what they mean: passing a value here and expecting it to be drawn
        /// first silently shifts every assertion by one draw.
        /// </param>
        /// <param name="values">Consumed in order, one per draw.</param>
        internal LocationScriptedRng(float repeatAfterScript, params float[] values)
        {
            _repeatAfterScript = repeatAfterScript;
            _script = new Queue<float>(values ?? Array.Empty<float>());
        }

        /// <summary>Every draw returns <paramref name="value"/>.</summary>
        internal static LocationScriptedRng Always(float value) => new LocationScriptedRng(value);

        /// <summary>These values in order, then the last one forever.</summary>
        internal static LocationScriptedRng Sequence(params float[] values)
        {
            if (values == null || values.Length == 0)
                throw new ArgumentException("Sequence needs at least one value.", nameof(values));
            return new LocationScriptedRng(values[values.Length - 1], values);
        }

        /// <summary>Total draws taken. Asserted by the draw-count tests.</summary>
        internal int Draws { get; private set; }

        private float Next()
        {
            Draws++;
            return _script.Count > 0 ? _script.Dequeue() : _repeatAfterScript;
        }

        private static int FloorToSpan(float value, int span)
        {
            if (span <= 0) return 0;
            int v = (int)(value * span);
            if (v < 0) return 0;
            return v >= span ? span - 1 : v;
        }

        public uint NextUInt() => (uint)(Next() * 4294967295f);
        public float NextFloat() => Next();
        public int NextInt(int maxExclusive) => FloorToSpan(Next(), maxExclusive);
        public int Range(int minInclusive, int maxExclusive) =>
            minInclusive + FloorToSpan(Next(), maxExclusive - minInclusive);
        public float Range(float min, float max) => min + Next() * (max - min);
        public bool Chance(float probability) => Next() < probability;
        public void Shuffle<T>(IList<T> list) { }
        public IRngService GetStream(RngStream stream, int? salt = null) => this;
    }

    /// <summary>
    /// Covers <see cref="LocationEnemySelector.RandomUnit"/> — the port of AS3
    /// <c>Location.randomUnit(param1, param2)</c> (<c>Location.as:1425-1726</c>).
    ///
    /// <para>This function is the reason random rooms were empty: it is the step that turns an authored
    /// enemy <i>point group</i> (<c>enl1</c>/<c>enl2</c>/<c>enf1</c>/<c>enc1</c>/<c>lov</c>) into a
    /// concrete enemy <i>family</i>. The port had no such function, so the group ids went straight to
    /// <c>Unit.create</c>, where no unit of that name exists.</para>
    /// </summary>
    [TestFixture]
    public class LocationEnemySelectorTests
    {
        private static LocationEnemyContext Ctx(
            int biome = -1, int tipEnemy = -1, float difficulty = 0f, int landX = 0, int landY = 0)
            => new LocationEnemyContext(biome, tipEnemy, difficulty, landX, landY);

        private static string Pick(string groupId, in LocationEnemyContext ctx, float value = 0f,
            bool water = false, int expectedDraws = -1)
        {
            var rng = LocationScriptedRng.Always(value);
            string got = LocationEnemySelector.RandomUnit(groupId, ctx, water, rng);
            if (expectedDraws >= 0)
            {
                Assert.That(rng.Draws, Is.EqualTo(expectedDraws),
                    $"'{groupId}' should spend exactly {expectedDraws} draw(s) of the spawn stream.");
            }
            return got;
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // The group table
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>tipEn:Array = ["","enl1","enl2","enf1","enc1","lov"]</c> (<c>Location.as:221</c>) — the
        /// mapping from <c>kolEn</c> index to group id. Index 0 is the unused slot, which is why
        /// <c>setRandomUnits</c> loops from 1.
        /// </summary>
        [Test]
        public void GroupIds_MatchTheOracleTipEnTable()
        {
            Assert.That(LocationEnemySelector.GroupCount, Is.EqualTo(6));
            Assert.That(LocationEnemySelector.GroupIds.Length, Is.EqualTo(LocationEnemySelector.GroupCount));
            Assert.That(LocationEnemySelector.GroupIds[0], Is.EqualTo(string.Empty));
            Assert.That(LocationEnemySelector.GroupIds[1], Is.EqualTo("enl1"));
            Assert.That(LocationEnemySelector.GroupIds[2], Is.EqualTo("enl2"));
            Assert.That(LocationEnemySelector.GroupIds[3], Is.EqualTo("enf1"));
            Assert.That(LocationEnemySelector.GroupIds[4], Is.EqualTo("enc1"));
            Assert.That(LocationEnemySelector.GroupIds[5], Is.EqualTo("lov"));
        }

        /// <summary>
        /// <c>tipn</c> is read off the AllData row in the oracle and off the id in the port. The lookup
        /// is <b>ordinal</b>, because AS3's ids are lower-case and a case-insensitive match would map a
        /// hypothetical <c>ENL1</c> onto the <c>enl1</c> group that AS3 would not have found.
        /// </summary>
        [Test]
        public void GroupIndexForId_MapsOnlyTheFiveMarkers_AndIsCaseSensitive()
        {
            Assert.That(LocationEnemySelector.GroupIndexForId("enl1"), Is.EqualTo(1));
            Assert.That(LocationEnemySelector.GroupIndexForId("enl2"), Is.EqualTo(2));
            Assert.That(LocationEnemySelector.GroupIndexForId("enf1"), Is.EqualTo(3));
            Assert.That(LocationEnemySelector.GroupIndexForId("enc1"), Is.EqualTo(4));
            Assert.That(LocationEnemySelector.GroupIndexForId("lov"), Is.EqualTo(5));

            Assert.That(LocationEnemySelector.GroupIndexForId("enl3"), Is.EqualTo(0));
            Assert.That(LocationEnemySelector.GroupIndexForId(string.Empty), Is.EqualTo(0));
            Assert.That(LocationEnemySelector.GroupIndexForId(null), Is.EqualTo(0));
            Assert.That(LocationEnemySelector.GroupIndexForId("ENL1"), Is.EqualTo(0));
            Assert.That(LocationEnemySelector.GroupIndexForId("raider"), Is.EqualTo(0));

            Assert.That(LocationEnemySelector.IsGroupId("lov"), Is.True);
            Assert.That(LocationEnemySelector.IsGroupId("slmine"), Is.False);
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Neutral context — the default answer of every arm
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// With no biome, no tipEnemy and difficulty 0, each group still has to answer something. Four
        /// of the five produce a family; <c>enf1</c> produces the empty string, which is the oracle's
        /// real answer and is what makes <c>createUnit</c>'s fallback arm necessary.
        /// </summary>
        [Test]
        public void RandomUnit_NeutralContext_ResolvesEachGroupToItsDocumentedFamily()
        {
            LocationEnemyContext ctx = Ctx();

            Assert.That(Pick("enl2", ctx, 0f, expectedDraws: 0), Is.EqualTo("raider"));
            Assert.That(Pick("enc1", ctx, 0f, expectedDraws: 0), Is.EqualTo("turret"));
            Assert.That(Pick("lov", ctx, 0f, expectedDraws: 0), Is.EqualTo("mine"));
            Assert.That(Pick("enl1", ctx, 0f, expectedDraws: 1), Is.EqualTo("tarakan"));
            Assert.That(Pick("enf1", ctx, 0f, expectedDraws: 0), Is.EqualTo(string.Empty));
        }

        /// <summary>
        /// The three <c>return ""</c> arms (<c>Location.as:1544-1547</c>, <c>:1649-1652</c>,
        /// <c>:1684-1687</c>) plus the biome-10 <c>enf1</c> arm. The two positive controls matter as
        /// much as the negatives: <c>enl2</c> and <c>enc1</c> are <i>not</i> guarded in biomes 10/11, so
        /// a port that declined for every group in those biomes would pass the negative half alone.
        /// </summary>
        [Test]
        public void RandomUnit_BiomesTenAndEleven_DeclineOnlyForTheThreeGuardedGroups()
        {
            Assert.That(Pick("enl1", Ctx(biome: 10), 0f), Is.EqualTo(string.Empty));
            Assert.That(Pick("enl1", Ctx(biome: 11), 0f), Is.EqualTo(string.Empty));
            Assert.That(Pick("enf1", Ctx(biome: 10), 0f), Is.EqualTo(string.Empty));
            Assert.That(Pick("lov", Ctx(biome: 10), 0f), Is.EqualTo(string.Empty));
            Assert.That(Pick("lov", Ctx(biome: 11), 0f), Is.EqualTo(string.Empty));

            // Positive controls: these two groups are unguarded and must still answer.
            Assert.That(Pick("enl2", Ctx(biome: 10), 0f), Is.EqualTo("stabpon"));
            Assert.That(Pick("enc1", Ctx(biome: 10), 0f), Is.EqualTo("turret"));
            Assert.That(Pick("enl2", Ctx(biome: 11), 0f), Is.EqualTo("raider"));
            Assert.That(Pick("enc1", Ctx(biome: 11), 0f), Is.EqualTo("turret"));

            // And enf1 is only guarded for biome 10, not 11.
            Assert.That(Pick("enf1", Ctx(biome: 11), 0f), Is.EqualTo(string.Empty));
        }

        /// <summary>
        /// An id that is not one of the five groups. The oracle's <c>switch</c> has no <c>default</c>
        /// clause, so <c>_loc3_</c> stays <c>""</c> — the same answer the biome-10 arms give, and the
        /// reason the caller must treat <c>""</c> as "create the group id itself" rather than "nothing".
        /// </summary>
        [Test]
        public void RandomUnit_AnIdThatIsNotAMarkerGroup_ReturnsEmpty()
        {
            LocationEnemyContext ctx = Ctx();
            Assert.That(Pick("enl3", ctx, 0f, expectedDraws: 0), Is.EqualTo(string.Empty));
            Assert.That(Pick(string.Empty, ctx, 0f, expectedDraws: 0), Is.EqualTo(string.Empty));
            Assert.That(Pick("raider", ctx, 0f, expectedDraws: 0), Is.EqualTo(string.Empty));
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Water
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>(biom == 1 || biom == 5) &amp;&amp; param2</c> — water wins over every later arm, in both
        /// water biomes and for all three water-aware groups. The dry controls show the arm is gated on
        /// <c>param2</c> and not merely on the biome.
        /// </summary>
        [Test]
        public void RandomUnit_WaterInBiomesOneAndFive_SelectsFishForEveryWaterAwareGroup()
        {
            foreach (int biome in new[] { 1, 5 })
            {
                Assert.That(Pick("enl1", Ctx(biome: biome), 0.9f, water: true), Is.EqualTo("fish"),
                    $"enl1 in biome {biome} over water");
                Assert.That(Pick("enc1", Ctx(biome: biome), 0.9f, water: true), Is.EqualTo("fish"),
                    $"enc1 in biome {biome} over water");
                Assert.That(Pick("enf1", Ctx(biome: biome), 0.9f, water: true), Is.EqualTo("fish"),
                    $"enf1 in biome {biome} over water");
            }

            // Dry controls — same biome, param2 false.
            Assert.That(Pick("enc1", Ctx(biome: 1), 0.9f, water: false), Is.EqualTo("turret"));
            Assert.That(Pick("enf1", Ctx(biome: 1), 0.9f, water: false), Is.EqualTo(string.Empty));
            Assert.That(Pick("enc1", Ctx(biome: 5), 0.9f, water: false), Is.EqualTo("bloodwing"));
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Biome overrides — the arms that run before any tipEnemy ladder
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Biome 5 answers before <c>tipEnemy</c> is consulted at all, and <c>enl1</c>'s arm rolls. A
        /// tipEnemy of 0 would otherwise send <c>enl1</c> down the parity arm (<c>slime</c>/<c>ant</c>)
        /// and <c>lov</c> to <c>slmine</c>/<c>trap</c>.
        /// </summary>
        [Test]
        public void RandomUnit_BiomeFive_PicksTheBiomeFamilyBeforeAnyTipEnemyLadder()
        {
            LocationEnemyContext ctx = Ctx(biome: 5, tipEnemy: 0);

            Assert.That(Pick("enl1", ctx, 0.9f), Is.EqualTo("slime"));
            Assert.That(Pick("enl1", ctx, 0.1f), Is.EqualTo("scorp3"));
            Assert.That(Pick("enc1", ctx, 0.9f), Is.EqualTo("bloodwing"));
            Assert.That(Pick("enf1", ctx, 0.9f), Is.EqualTo("bloat"));
            Assert.That(Pick("lov", ctx, 0.9f), Is.EqualTo("slmine"));
        }

        /// <summary>
        /// Biome 6 short-circuits <c>enl1</c> to <c>roller</c> and biome 6/4 short-circuits <c>enc1</c>
        /// to <c>cturret</c>. The paired control uses the same <c>tipEnemy</c> with no biome, where the
        /// parity arm answers instead — so the test fails if the biome guard is dropped.
        /// </summary>
        [Test]
        public void RandomUnit_BiomesFourAndSix_TakeTheBiomeFamilyEvenWhenTipEnemyWouldSayOtherwise()
        {
            Assert.That(Pick("enl1", Ctx(biome: 6, tipEnemy: 0), 0.9f), Is.EqualTo("roller"));
            Assert.That(Pick("enl1", Ctx(biome: -1, tipEnemy: 0), 0.9f), Is.EqualTo("slime"));

            Assert.That(Pick("enc1", Ctx(biome: 6, tipEnemy: 0), 0.9f), Is.EqualTo("cturret"));
            Assert.That(Pick("enc1", Ctx(biome: 4, tipEnemy: 0), 0.9f), Is.EqualTo("cturret"));
            Assert.That(Pick("enc1", Ctx(biome: -1, tipEnemy: 0), 0.9f), Is.EqualTo("bloodwing"));
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // enl1 — parity, the rat override, and the two-draw structure
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>(landX + landY) % 2 == 0</c> alternates the family down a land. Both sites that use it in
        /// <c>enl1</c> are covered: the <c>roller</c>/<c>msp</c> arm and the <c>slime</c>/<c>ant</c> arm.
        /// </summary>
        [Test]
        public void RandomUnit_Enl1_ParityAlternatesTheFamily()
        {
            Assert.That(Pick("enl1", Ctx(tipEnemy: 7, landX: 0, landY: 0), 0f), Is.EqualTo("roller"));
            Assert.That(Pick("enl1", Ctx(tipEnemy: 7, landX: 1, landY: 0), 0f), Is.EqualTo("msp"));

            Assert.That(Pick("enl1", Ctx(tipEnemy: 0, landX: 0, landY: 0), 0.9f), Is.EqualTo("slime"));
            Assert.That(Pick("enl1", Ctx(tipEnemy: 0, landX: 1, landY: 0), 0.9f), Is.EqualTo("ant"));
        }

        /// <summary>
        /// The biome-1 rat override is a <b>second draw taken after the family has already been
        /// chosen</b> (<c>Location.as:1592-1595</c>), not an extra rung of the same ladder. The
        /// draw-count assertion is the point of this test: a port that folded the override into the
        /// first roll would pick the same families on average but would desynchronise the stream.
        /// </summary>
        [Test]
        public void RandomUnit_Enl1_BiomeOneRatOverride_SpendsASecondDrawAndReplacesTheFamily()
        {
            LocationEnemyContext biomeOne = Ctx(biome: 1, tipEnemy: 0);
            LocationEnemyContext noBiome = Ctx(tipEnemy: 0);

            // biome 1, override hits: one draw (the override), family replaced.
            Assert.That(Pick("enl1", biomeOne, 0.1f, expectedDraws: 1), Is.EqualTo("rat"));

            // biome 1, override misses: still one draw, family kept.
            Assert.That(Pick("enl1", biomeOne, 0.9f, expectedDraws: 1), Is.EqualTo("slime"));

            // No biome: the override arm is not reached, so no draw at all.
            Assert.That(Pick("enl1", noBiome, 0.1f, expectedDraws: 0), Is.EqualTo("slime"));
        }

        /// <summary>
        /// The tail of <c>enl1</c>: <c>molerat</c> is gated on level ≥ 2 and then <c>tarakan</c> on a
        /// 60 % roll, with <c>rat</c> as the fallthrough. Below level 2 the <c>molerat</c> rung must
        /// <b>not draw at all</b>, which shifts every later placement if it does.
        /// </summary>
        [Test]
        public void RandomUnit_Enl1_TailLadder_IsGatedOnLevelTwo()
        {
            // tipEnemy 1 is not 0/5/6, so the parity arm is skipped entirely.
            LocationEnemyContext lowLevel = Ctx(tipEnemy: 1, difficulty: 1f);
            LocationEnemyContext highLevel = Ctx(tipEnemy: 1, difficulty: 5f);

            // level 1: molerat arm does not draw; only the tarakan roll happens.
            Assert.That(Pick("enl1", lowLevel, 0.1f, expectedDraws: 1), Is.EqualTo("tarakan"));
            Assert.That(Pick("enl1", lowLevel, 0.9f, expectedDraws: 1), Is.EqualTo("rat"));

            // level 5: molerat arm draws first and wins.
            Assert.That(Pick("enl1", highLevel, 0.1f, expectedDraws: 1), Is.EqualTo("molerat"));

            // level 5, molerat roll misses (0.1 * 5 = 0.5 threshold), then tarakan roll hits.
            Assert.That(Pick("enl1", highLevel, 0.9f, expectedDraws: 2), Is.EqualTo("rat"));
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // enl2 — the tipEnemy ladder
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The <c>tipEnemy == 2</c> ladder is ordered and each rung is short-circuited by its own
        /// difficulty guard. The four assertions pin: the order (a matching high rung beats a matching
        /// low one), the level-12 gate on <c>eqd</c>, and that a full miss terminates in <c>robot</c>.
        /// </summary>
        [Test]
        public void RandomUnit_Enl2_RobotLadder_IsOrderedAndTerminatesInRobot()
        {
            LocationEnemyContext robotTier = Ctx(tipEnemy: 2);

            // Every rung misses: three draws, then the fallthrough.
            Assert.That(Pick("enl2", Ctx(tipEnemy: 2, difficulty: 20f), 0.99f, expectedDraws: 3),
                Is.EqualTo("robot"));

            // A low roll at level 20 hits the FIRST eligible rung (landturret), not eqd —
            // eqd additionally requires biome 2.
            Assert.That(Pick("enl2", Ctx(tipEnemy: 2, difficulty: 20f), 0.0f, expectedDraws: 1),
                Is.EqualTo("landturret"));

            // Biome 2 at level 12: the eqd rung opens and wins.
            Assert.That(Pick("enl2", Ctx(biome: 2, tipEnemy: 2, difficulty: 12f), 0.0f, expectedDraws: 1),
                Is.EqualTo("eqd"));

            // Biome 2 just below level 12: the rung is closed, so the SAME roll falls through to
            // landturret instead. This is the assertion that fails if the `>= 12` guard is dropped.
            Assert.That(Pick("enl2", Ctx(biome: 2, tipEnemy: 2, difficulty: 11.99f), 0.0f, expectedDraws: 1),
                Is.EqualTo("landturret"));

            // Control: no rung matches at all below level 2.
            Assert.That(Pick("enl2", robotTier, 0.0f, expectedDraws: 0), Is.EqualTo("robot"));
        }

        /// <summary>
        /// <c>tipEnemy == 9</c> is the one arm with two sequential rungs at 10 % and 7 %. The second is
        /// only reachable when the first misses, and a miss on both ends in <c>encl</c> — three
        /// different outcomes from three different draw counts.
        /// </summary>
        [Test]
        public void RandomUnit_Enl2_TipEnemyNine_RunsTheTwoRungsInOrder()
        {
            LocationEnemyContext ctx = Ctx(tipEnemy: 9);

            Assert.That(Pick("enl2", ctx, 0.05f, expectedDraws: 1), Is.EqualTo("hellhound"));

            // First roll misses, second hits: two draws, and the first rung is NOT retried.
            var second = LocationScriptedRng.Sequence(0.5f, 0.05f);
            Assert.That(LocationEnemySelector.RandomUnit("enl2", ctx, false, second), Is.EqualTo("landturret"));
            Assert.That(second.Draws, Is.EqualTo(2));

            var third = LocationScriptedRng.Sequence(0.5f, 0.5f);
            Assert.That(LocationEnemySelector.RandomUnit("enl2", ctx, false, third), Is.EqualTo("encl"));
            Assert.That(third.Draws, Is.EqualTo(2));
        }

        /// <summary>
        /// <c>tipEnemy == 8</c> is the zombie/necros split, and <c>tipEnemy == 7</c> nests a second draw
        /// inside the first — so a miss on the outer roll costs <i>one</i> draw and a hit costs two.
        /// </summary>
        [Test]
        public void RandomUnit_Enl2_TipEnemySevenAndEight_NestedDrawCounts()
        {
            Assert.That(Pick("enl2", Ctx(tipEnemy: 8), 0.5f, expectedDraws: 1), Is.EqualTo("zombie"));
            Assert.That(Pick("enl2", Ctx(tipEnemy: 8), 0.9f, expectedDraws: 1), Is.EqualTo("necros"));

            // Outer roll misses (0.5 >= 0.4): one draw, ranger.
            Assert.That(Pick("enl2", Ctx(tipEnemy: 7), 0.5f, expectedDraws: 1), Is.EqualTo("ranger"));

            // Outer roll hits and the inner roll also hits: two draws, gutsy.
            Assert.That(Pick("enl2", Ctx(tipEnemy: 7), 0.3f, expectedDraws: 2), Is.EqualTo("gutsy"));

            // Outer roll hits but the inner roll misses: two draws, protect.
            var protect = LocationScriptedRng.Sequence(0.3f, 0.6f);
            Assert.That(LocationEnemySelector.RandomUnit("enl2", Ctx(tipEnemy: 7), false, protect),
                Is.EqualTo("protect"));
            Assert.That(protect.Draws, Is.EqualTo(2));
        }

        /// <summary>
        /// The <c>enl2</c> arms that take no draw at all. <c>tipEnemy == 0</c> is the zombie room,
        /// <c>3</c> the slaver room, <c>4</c> the merc room, <c>5</c> the alicorn room, <c>6</c> the
        /// zebra room, <c>10</c> the hellhound room.
        /// </summary>
        [Test]
        public void RandomUnit_Enl2_SingleAnswerTiers_TakeNoDraw()
        {
            Assert.That(Pick("enl2", Ctx(tipEnemy: 0), 0f, expectedDraws: 0), Is.EqualTo("zombie"));
            Assert.That(Pick("enl2", Ctx(tipEnemy: 3), 0f, expectedDraws: 0), Is.EqualTo("slaver"));
            Assert.That(Pick("enl2", Ctx(tipEnemy: 4), 0f, expectedDraws: 0), Is.EqualTo("merc"));
            Assert.That(Pick("enl2", Ctx(tipEnemy: 5), 0f, expectedDraws: 0), Is.EqualTo("alicorn"));
            Assert.That(Pick("enl2", Ctx(tipEnemy: 6), 0f, expectedDraws: 0), Is.EqualTo("zebra"));
            Assert.That(Pick("enl2", Ctx(tipEnemy: 10), 0f, expectedDraws: 0), Is.EqualTo("hellhound"));
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // enc1 / enf1 / lov
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>enc1</c>'s tipEnemy triplet plus the biome-1 slime override, which — unlike
        /// <c>enl1</c>'s — is the <i>same</i> draw as the family choice, not a second one.
        /// </summary>
        [Test]
        public void RandomUnit_Enc1_TipEnemyTripletAndTheBiomeOneSlimeOverride()
        {
            Assert.That(Pick("enc1", Ctx(tipEnemy: 0), 0.9f, expectedDraws: 0), Is.EqualTo("bloodwing"));
            Assert.That(Pick("enc1", Ctx(biome: 1, tipEnemy: 0), 0.1f, expectedDraws: 1), Is.EqualTo("slime"));
            Assert.That(Pick("enc1", Ctx(biome: 1, tipEnemy: 0), 0.9f, expectedDraws: 1), Is.EqualTo("bloodwing"));

            Assert.That(Pick("enc1", Ctx(tipEnemy: 1), 0.9f, expectedDraws: 0), Is.EqualTo("turret"));
            Assert.That(Pick("enc1", Ctx(tipEnemy: 5), 0.9f, expectedDraws: 0), Is.EqualTo("bloodwing"));
            Assert.That(Pick("enc1", Ctx(tipEnemy: 6), 0.9f, expectedDraws: 0), Is.EqualTo("bloodwing"));
        }

        /// <summary>
        /// <b>The <c>tipEnemy == 2</c> clause of <c>enf1</c>'s dron rung is unreachable.</b> The
        /// <c>spritebot</c> arm above it fires for every <c>tipEnemy == 2</c> with level ≥ 3, while the
        /// dron clause demands level &gt; 10 — a strict superset. This is faithful to the oracle
        /// (<c>Location.as:1673-1678</c>) and is recorded here so nobody "fixes" it by loosening the
        /// spritebot guard.
        /// </summary>
        [Test]
        public void RandomUnit_Enf1_TipEnemyTwo_CanNeverReachTheDronRung()
        {
            // Level 50, tipEnemy 2: still spritebot, and the dron rung's roll is never taken.
            Assert.That(Pick("enf1", Ctx(tipEnemy: 2, difficulty: 50f), 0.0f, expectedDraws: 0),
                Is.EqualTo("spritebot"));

            // Below level 3 nothing matches at all — the empty answer.
            Assert.That(Pick("enf1", Ctx(tipEnemy: 2, difficulty: 2f), 0.0f, expectedDraws: 0),
                Is.EqualTo(string.Empty));

            // The rung IS reachable for the other four tiers.
            Assert.That(Pick("enf1", Ctx(tipEnemy: 7), 0.0f, expectedDraws: 0), Is.EqualTo("dron"));
            Assert.That(Pick("enf1", Ctx(tipEnemy: 9), 0.0f, expectedDraws: 0), Is.EqualTo("dron"));
            Assert.That(Pick("enf1", Ctx(tipEnemy: 10), 0.0f, expectedDraws: 0), Is.EqualTo("dron"));
            Assert.That(Pick("enf1", Ctx(tipEnemy: 11), 0.0f, expectedDraws: 0), Is.EqualTo("dron"));
        }

        /// <summary>
        /// <c>enf1</c>'s bloat and vortex arms. <c>vortex</c> needs both a tier in
        /// <c>{1,3,4,6}</c> and level ≥ 3; below level 3 that tier set falls through to the empty answer.
        /// </summary>
        [Test]
        public void RandomUnit_Enf1_BloatAndVortexArms()
        {
            Assert.That(Pick("enf1", Ctx(tipEnemy: 0), 0f, expectedDraws: 0), Is.EqualTo("bloat"));
            Assert.That(Pick("enf1", Ctx(tipEnemy: 5), 0f, expectedDraws: 0), Is.EqualTo("bloat"));

            Assert.That(Pick("enf1", Ctx(tipEnemy: 1, difficulty: 3f), 0f, expectedDraws: 0), Is.EqualTo("vortex"));
            Assert.That(Pick("enf1", Ctx(tipEnemy: 3, difficulty: 3f), 0f, expectedDraws: 0), Is.EqualTo("vortex"));
            Assert.That(Pick("enf1", Ctx(tipEnemy: 4, difficulty: 3f), 0f, expectedDraws: 0), Is.EqualTo("vortex"));
            Assert.That(Pick("enf1", Ctx(tipEnemy: 6, difficulty: 3f), 0f, expectedDraws: 0), Is.EqualTo("vortex"));

            // Below the level-3 gate the same tiers give nothing.
            Assert.That(Pick("enf1", Ctx(tipEnemy: 1, difficulty: 2.99f), 0f, expectedDraws: 0),
                Is.EqualTo(string.Empty));
        }

        /// <summary>
        /// <c>lov</c>'s parity arms: <c>slmine</c>/<c>trap</c> at tier 0, and the three-way
        /// <c>trridge</c>/<c>trplate</c>/<c>trcans</c> split that only opens above level 10.
        /// </summary>
        [Test]
        public void RandomUnit_Lov_TierZeroParityAndTheThreeWayTrapSplit()
        {
            Assert.That(Pick("lov", Ctx(tipEnemy: 0, landX: 0), 0f, expectedDraws: 0), Is.EqualTo("slmine"));
            Assert.That(Pick("lov", Ctx(tipEnemy: 0, landX: 1), 0f, expectedDraws: 0), Is.EqualTo("trap"));

            Assert.That(Pick("lov", Ctx(tipEnemy: 1, difficulty: 10f), 0.2f, expectedDraws: 1),
                Is.EqualTo("trridge"));

            // Just below level 10 the trridge rung is closed, so the SAME roll falls through to
            // trplate — one draw fewer overall, because the guard short-circuits.
            Assert.That(Pick("lov", Ctx(tipEnemy: 1, difficulty: 9.99f), 0.2f, expectedDraws: 1),
                Is.EqualTo("trplate"));

            // Both rolls miss: trcans, on two draws.
            Assert.That(Pick("lov", Ctx(tipEnemy: 1, difficulty: 10f), 0.9f, expectedDraws: 2),
                Is.EqualTo("trcans"));

            // The whole arm is parity-gated.
            Assert.That(Pick("lov", Ctx(tipEnemy: 1, landX: 1), 0.2f), Is.EqualTo("mine"));
            Assert.That(Pick("lov", Ctx(tipEnemy: 3, landX: 1), 0.2f), Is.EqualTo("mine"));
            Assert.That(Pick("lov", Ctx(tipEnemy: 4, landX: 1), 0.2f), Is.EqualTo("mine"));
            Assert.That(Pick("lov", Ctx(tipEnemy: 6, landX: 1), 0.2f), Is.EqualTo("mine"));
        }

        /// <summary>
        /// <c>trlaser</c> needs <c>(biome 2 AND tier 2) OR tier 7 OR tier 9</c>, and parity. The biome-3
        /// control is the one that fails if the <c>&amp;&amp;</c> is mis-parenthesised.
        /// </summary>
        [Test]
        public void RandomUnit_Lov_Trlaser_NeedsBiomeTwoWithTierTwo_OrTierSevenOrNine()
        {
            Assert.That(Pick("lov", Ctx(biome: 2, tipEnemy: 2, landX: 0), 0f, expectedDraws: 0),
                Is.EqualTo("trlaser"));
            Assert.That(Pick("lov", Ctx(biome: 3, tipEnemy: 2, landX: 0), 0f, expectedDraws: 0),
                Is.EqualTo("mine"));
            Assert.That(Pick("lov", Ctx(tipEnemy: 7, landX: 0), 0f, expectedDraws: 0), Is.EqualTo("trlaser"));
            Assert.That(Pick("lov", Ctx(tipEnemy: 9, landX: 0), 0f, expectedDraws: 0), Is.EqualTo("trlaser"));

            // Parity is part of the guard, not an afterthought.
            Assert.That(Pick("lov", Ctx(tipEnemy: 7, landX: 1), 0f, expectedDraws: 0), Is.EqualTo("mine"));
        }
    }

    /// <summary>
    /// Covers <see cref="RoomDifficulty.SetEnemyCount"/> — the port of AS3
    /// <c>Location.setKolEn(param1, param2, param3, param4 = 0)</c> (<c>Location.as:1037-1051</c>).
    ///
    /// <para>This method is the <b>only</b> writer of <c>enemyCounts</c>, and before the fix it had no
    /// production caller at all — so <c>GetTotalEnemyCount()</c> was permanently <c>0</c> and the
    /// random-enemy pass had nothing to place. Its two arms also draw a different number of times from
    /// the shared spawn stream, which is why the draw counts are asserted rather than just the values.</para>
    /// </summary>
    [TestFixture]
    public class RoomDifficultySetEnemyCountTests
    {
        /// <summary>
        /// AS3 <c>setKolEn(-1, min, max)</c> writes <c>kolEnSpawn</c> — the alarm-point count, a
        /// different seam — and must not touch any <c>kolEn</c> slot. It also takes exactly one draw:
        /// the <c>-1</c> arm has no <c>param4</c> bonus roll.
        /// </summary>
        [Test]
        public void SetEnemyCount_SpawnCountSlot_WritesKolEnSpawnAndLeavesEverySlotUntouched()
        {
            var difficulty = new RoomDifficulty();
            var rng = LocationScriptedRng.Always(0.5f);

            difficulty.SetEnemyCount(RoomDifficulty.SpawnCountSlot, 2, 3, 0, rng);

            // 2 + floor(0.5 * (3 - 2 + 1)) == 3
            Assert.That(difficulty.spawnEnemyCount, Is.EqualTo(3));
            Assert.That(rng.Draws, Is.EqualTo(1));

            for (int slot = 0; slot < difficulty.enemyCounts.Length; slot++)
            {
                Assert.That(difficulty.enemyCounts[slot], Is.EqualTo(0),
                    $"slot {slot} must be untouched by the kolEnSpawn arm");
            }
            Assert.That(difficulty.GetTotalEnemyCount(), Is.EqualTo(0));
        }

        /// <summary>
        /// AS3's <c>min + floor(random * (max - min + 1))</c> is inclusive at <b>both</b> ends. A port
        /// that used a half-open range would drop the maximum, which at these counts is a whole enemy
        /// per group per room.
        /// </summary>
        [Test]
        public void SetEnemyCount_RangeIsInclusiveAtBothEnds()
        {
            var low = new RoomDifficulty();
            low.SetEnemyCount(2, 2, 4, 0, LocationScriptedRng.Always(0.0f));
            Assert.That(low.GetEnemyCount(2), Is.EqualTo(2));

            var high = new RoomDifficulty();
            high.SetEnemyCount(2, 2, 4, 0, LocationScriptedRng.Always(0.999f));
            Assert.That(high.GetEnemyCount(2), Is.EqualTo(4));
        }

        /// <summary>
        /// With no bonus the arm spends exactly one draw, even if the stream has more values available.
        /// </summary>
        [Test]
        public void SetEnemyCount_WithoutABonus_SpendsExactlyOneDraw()
        {
            var difficulty = new RoomDifficulty();
            var rng = LocationScriptedRng.Sequence(0.5f, 0.1f);

            difficulty.SetEnemyCount(3, 3, 4, 0, rng);

            // 3 + floor(0.5 * (4 - 3 + 1)) == 4
            Assert.That(difficulty.GetEnemyCount(3), Is.EqualTo(4));
            Assert.That(rng.Draws, Is.EqualTo(1), "the second scripted value must never be consumed");
        }

        /// <summary>
        /// The <c>param4 &gt; 0</c> bonus costs a <b>second draw even when the bonus misses</b>
        /// (<c>Location.as:1046</c>). That is the whole reason the two arms differ in draw count.
        /// </summary>
        [Test]
        public void SetEnemyCount_WithABonus_SpendsASecondDrawEvenWhenTheBonusMisses()
        {
            var difficulty = new RoomDifficulty();
            var rng = LocationScriptedRng.Sequence(0.5f, 0.9f);

            difficulty.SetEnemyCount(3, 3, 4, 2, rng);

            Assert.That(difficulty.GetEnemyCount(3), Is.EqualTo(4), "0.9 is not < 0.2, so no bonus");
            Assert.That(rng.Draws, Is.EqualTo(2));
        }

        /// <summary>And the bonus is added when that second draw is under 20 %.</summary>
        [Test]
        public void SetEnemyCount_WithABonus_AddsItWhenTheSecondDrawIsUnderTwentyPercent()
        {
            var difficulty = new RoomDifficulty();
            var rng = LocationScriptedRng.Sequence(0.5f, 0.1f);

            difficulty.SetEnemyCount(3, 3, 4, 2, rng);

            // 4 from the range roll, +2 for the bonus.
            Assert.That(difficulty.GetEnemyCount(3), Is.EqualTo(6));
            Assert.That(rng.Draws, Is.EqualTo(2));
        }

        /// <summary>
        /// Slots outside 1..5 are ignored <b>without drawing</b>. AS3 would write past the array; the
        /// port declines instead, and the important half is that it does not spend a draw while doing so.
        /// </summary>
        [Test]
        public void SetEnemyCount_ASlotOutsideOneToFive_IsIgnoredWithoutDrawing()
        {
            var difficulty = new RoomDifficulty();
            var rng = LocationScriptedRng.Always(0.5f);

            difficulty.SetEnemyCount(0, 1, 1, 0, rng);
            difficulty.SetEnemyCount(6, 1, 1, 0, rng);
            difficulty.SetEnemyCount(-2, 1, 1, 0, rng);

            Assert.That(rng.Draws, Is.EqualTo(0));
            Assert.That(difficulty.GetTotalEnemyCount(), Is.EqualTo(0));
            Assert.That(difficulty.spawnEnemyCount, Is.EqualTo(0));
        }

        /// <summary>
        /// The total sums slots 1..5 only — slot 0 is the unused one, and <c>kolEnSpawn</c> belongs to
        /// the alarm seam, so counting either would inflate the room's visible enemy total.
        /// </summary>
        [Test]
        public void GetTotalEnemyCount_SumsSlotsOneThroughFive_IgnoringSlotZeroAndTheAlarmCount()
        {
            var difficulty = new RoomDifficulty
            {
                spawnEnemyCount = 7,
                hiddenEnemyCount = 5
            };
            difficulty.enemyCounts[0] = 9;
            for (int slot = 1; slot <= 5; slot++) difficulty.enemyCounts[slot] = 1;

            Assert.That(difficulty.GetTotalEnemyCount(), Is.EqualTo(5));
            Assert.That(difficulty.GetEnemyCount(0), Is.EqualTo(0));
            Assert.That(difficulty.GetEnemyCount(6), Is.EqualTo(0));
        }

        /// <summary>
        /// The pre-fix state, pinned explicitly: a fresh <see cref="RoomDifficulty"/> carries no enemies,
        /// no alarm count and AS3's own <c>-1</c> "not set" tipEnemy. This is what every room looked like
        /// before <see cref="LocationDifficultyPlanner"/> existed, and it is the state the regression
        /// test below has to be able to leave behind.
        /// </summary>
        [Test]
        public void AFreshRoomDifficulty_HasNoEnemiesAndAnUnsetTipEnemy()
        {
            var difficulty = new RoomDifficulty();

            Assert.That(difficulty.GetTotalEnemyCount(), Is.EqualTo(0));
            Assert.That(difficulty.spawnEnemyCount, Is.EqualTo(0));
            Assert.That(difficulty.hiddenEnemyCount, Is.EqualTo(0));
            Assert.That(difficulty.tipEnemy, Is.EqualTo(-1));
            Assert.That(RoomDifficulty.SpawnCountSlot, Is.EqualTo(-1));
        }
    }

    /// <summary>
    /// Covers <see cref="LocationDifficultyPlanner.Apply"/> — the port of AS3
    /// <c>Land.setLocDif(param1, param2)</c> (<c>Land.as:980-1136</c>), the missing function that made
    /// random rooms empty.
    /// </summary>
    [TestFixture]
    public class LocationDifficultyPlannerTests
    {
        /// <summary>
        /// The five level fields all come from one number: <c>landDifLevel + depthOffset</c>
        /// (<c>Land.as:982-987</c>). Note <c>weaponLevel</c> is <c>1 + level/4</c> — the <c>1 +</c> is the
        /// part a hurried port drops.
        /// </summary>
        [Test]
        public void Apply_DerivesEveryLevelFieldFromTheLandLevelPlusTheDepthOffset()
        {
            var difficulty = new RoomDifficulty();

            LocationDifficultyPlanner.Apply(
                difficulty,
                new LocationDifficultyInput(biome: -1, landDifLevel: 8f, difficultyOffset: 2f),
                LocationScriptedRng.Always(0.99f));

            Assert.That(difficulty.baseDifficulty, Is.EqualTo(10f).Within(1e-4));
            Assert.That(difficulty.enemyLevel, Is.EqualTo(10f).Within(1e-4));
            Assert.That(difficulty.lockLevel, Is.EqualTo(7f).Within(1e-4));
            Assert.That(difficulty.mechLevel, Is.EqualTo(2.5f).Within(1e-4));
            Assert.That(difficulty.weaponLevel, Is.EqualTo(3.5f).Within(1e-4));
        }

        /// <summary>
        /// Every biome arm of the <c>tipEnemy</c> selection (<c>Land.as:996-1049</c>). The three
        /// single-answer biomes (1, 4, 11) must not draw for the tip itself, while biomes 5 and 6 roll a
        /// coin and biome 3 rolls a three-way.
        /// </summary>
        [Test]
        public void Apply_TipEnemyBiomeArms()
        {
            // biome 1 -> 0, biome 4 -> 7, biome 11 -> 11 (no tip roll of their own).
            Assert.That(TipFor(biome: 1, fallback: 0.99f), Is.EqualTo(0));
            Assert.That(TipFor(biome: 4, fallback: 0.99f), Is.EqualTo(7));
            Assert.That(TipFor(biome: 11, fallback: 0.99f), Is.EqualTo(11));

            // biome 5 -> 5 or 8
            Assert.That(TipFor(biome: 5, fallback: 0.1f), Is.EqualTo(5));
            Assert.That(TipFor(biome: 5, fallback: 0.5f), Is.EqualTo(8));

            // biome 6 -> 10 or 9 (note the inverted comparison: `random > 0.3` picks 9)
            Assert.That(TipFor(biome: 6, fallback: 0.1f), Is.EqualTo(10));
            Assert.That(TipFor(biome: 6, fallback: 0.5f), Is.EqualTo(9));

            // biome 3 -> floor(random * 3) + 3, i.e. 3 / 4 / 5
            Assert.That(TipFor(biome: 3, fallback: 0.0f), Is.EqualTo(3));
            Assert.That(TipFor(biome: 3, fallback: 0.5f), Is.EqualTo(4));
            Assert.That(TipFor(biome: 3, fallback: 0.999f), Is.EqualTo(5));
        }

        /// <summary>
        /// <c>if(param1.tipEnemy &lt; 0) param1.tipEnemy = floor(random * 3)</c> — the default roll. An
        /// authored <c>entip</c> suppresses that roll entirely, which is one draw fewer on the shared
        /// stream; that is what the draw-count comparison pins.
        /// </summary>
        [Test]
        public void Apply_AnAuthoredTipEnemy_SuppressesTheDefaultRoll()
        {
            var rolled = new RoomDifficulty();
            var rolledRng = LocationScriptedRng.Always(0.99f);
            LocationDifficultyPlanner.Apply(
                rolled,
                new LocationDifficultyInput(biome: -1, landDifLevel: 5f, difficultyOffset: 0f, tipEnemy: -1),
                rolledRng);

            var authored = new RoomDifficulty();
            var authoredRng = LocationScriptedRng.Always(0.99f);
            LocationDifficultyPlanner.Apply(
                authored,
                new LocationDifficultyInput(biome: -1, landDifLevel: 5f, difficultyOffset: 0f, tipEnemy: 6),
                authoredRng);

            // 0.99 -> floor(2.97) == 2, which is not 6 and is not 5, so the bands take the same shape.
            Assert.That(rolled.tipEnemy, Is.EqualTo(2));
            Assert.That(authored.tipEnemy, Is.EqualTo(6));
            Assert.That(rolledRng.Draws, Is.EqualTo(authoredRng.Draws + 1),
                "the default roll is the only difference between the two runs");
        }

        /// <summary>
        /// Biome 0 has a 25 % chance to force tier 1 <i>before</i> the default roll, and when it fires
        /// the default roll is skipped — so the "hit" case is one draw <b>shorter</b> than the miss case.
        /// </summary>
        [Test]
        public void Apply_BiomeZero_HasAQuarterChanceToForceTierOne()
        {
            var hit = new RoomDifficulty();
            var hitRng = LocationScriptedRng.Sequence(0.1f, 0.9f, 0.99f);
            LocationDifficultyPlanner.Apply(
                hit,
                new LocationDifficultyInput(biome: 0, landDifLevel: 0f, difficultyOffset: 0f),
                hitRng);
            Assert.That(hit.tipEnemy, Is.EqualTo(1));

            var miss = new RoomDifficulty();
            var missRng = LocationScriptedRng.Sequence(0.9f, 0.9f, 0.99f);
            LocationDifficultyPlanner.Apply(
                miss,
                new LocationDifficultyInput(biome: 0, landDifLevel: 0f, difficultyOffset: 0f),
                missRng);
            Assert.That(miss.tipEnemy, Is.EqualTo(2), "floor(0.9 * 3) == 2");

            Assert.That(hitRng.Draws, Is.EqualTo(missRng.Draws - 1),
                "forcing tier 1 replaces the default roll rather than preceding it");
        }

        /// <summary>
        /// <c>locDifLevel &gt; 12 &amp;&amp; biome in {0,2,3} &amp;&amp; random &lt; 0.1</c> sets tier 6
        /// (<c>Land.as:1016</c>). Both halves of the guard are needed: the same roll at exactly level 12
        /// must not fire, and must not even be drawn.
        /// </summary>
        [Test]
        public void Apply_TheHighLevelOverride_RequiresBothALevelAboveTwelveAndTheRightBiome()
        {
            var above = new RoomDifficulty();
            var aboveRng = LocationScriptedRng.Sequence(0.05f, 0.05f, 0.99f);
            LocationDifficultyPlanner.Apply(
                above,
                new LocationDifficultyInput(biome: 2, landDifLevel: 13f, difficultyOffset: 0f, tipEnemy: 1),
                aboveRng);
            Assert.That(above.tipEnemy, Is.EqualTo(6));

            var atBoundary = new RoomDifficulty();
            var boundaryRng = LocationScriptedRng.Sequence(0.05f, 0.05f, 0.99f);
            LocationDifficultyPlanner.Apply(
                atBoundary,
                new LocationDifficultyInput(biome: 2, landDifLevel: 12f, difficultyOffset: 0f, tipEnemy: 1),
                boundaryRng);

            // Level 12: the biome-2 arm still promotes tier 1 to tier 3, but the >12 arm is closed.
            Assert.That(atBoundary.tipEnemy, Is.EqualTo(3));
            Assert.That(aboveRng.Draws, Is.EqualTo(boundaryRng.Draws + 1),
                "the >12 guard must short-circuit without drawing at exactly 12");

            // And the biome half of the guard: biome 1 at level 20 keeps its forced tier 0.
            Assert.That(TipFor(biome: 1, fallback: 0.05f, landDifLevel: 20f), Is.EqualTo(0));
        }

        /// <summary>
        /// The three difficulty bands partition the level range at 4 and 10, and each band seeds a
        /// different <c>kolEnHid</c> spread. Asserting the hidden count is the cleanest way to prove
        /// which band ran, because the three spreads are disjoint (always 0 / 0-2 / 0-3).
        /// </summary>
        [Test]
        public void Apply_BandsPartitionTheLevelRange_AndTheHiddenCountFollowsTheBand()
        {
            // Band 1: level < 4 -> kolEnHid is forced to 0, and kolEnSpawn seeds 1..2.
            var bandOne = Plan(landDifLevel: 3.99f);
            Assert.That(bandOne.hiddenEnemyCount, Is.EqualTo(0));
            Assert.That(bandOne.spawnEnemyCount, Is.EqualTo(2));

            // Band 2: 4 <= level < 10 -> kolEnHid = floor(random * 3) == 2 at 0.99, kolEnSpawn 2..3.
            var bandTwo = Plan(landDifLevel: 4f);
            Assert.That(bandTwo.hiddenEnemyCount, Is.EqualTo(2));
            Assert.That(bandTwo.spawnEnemyCount, Is.EqualTo(3));

            // Band 3: level >= 10 -> kolEnHid = floor(random * 4) == 3 at 0.99, kolEnSpawn 2..4.
            var bandThree = Plan(landDifLevel: 10f);
            Assert.That(bandThree.hiddenEnemyCount, Is.EqualTo(3));
            Assert.That(bandThree.spawnEnemyCount, Is.EqualTo(4));
        }

        /// <summary>
        /// <c>if(param1.kolEnSpawn == 0)</c> guards the seeding of the alarm count
        /// (<c>Land.as:1061</c>, <c>:1088</c>, <c>:1115</c>). A room whose <c>kolspawn</c> option was
        /// authored must keep it — and, because the guard short-circuits, must also spend one draw fewer.
        /// </summary>
        [Test]
        public void Apply_TheKolEnSpawnGuard_SuppressesTheSeedDrawWhenTheRoomAlreadyCarriesOne()
        {
            var seeded = new RoomDifficulty { spawnEnemyCount = 3 };
            var seededRng = LocationScriptedRng.Always(0.99f);
            LocationDifficultyPlanner.Apply(
                seeded,
                new LocationDifficultyInput(biome: -1, landDifLevel: 5f, difficultyOffset: 0f),
                seededRng);

            var unseeded = new RoomDifficulty();
            var unseededRng = LocationScriptedRng.Always(0.99f);
            LocationDifficultyPlanner.Apply(
                unseeded,
                new LocationDifficultyInput(biome: -1, landDifLevel: 5f, difficultyOffset: 0f),
                unseededRng);

            Assert.That(seeded.spawnEnemyCount, Is.EqualTo(3), "an authored kolspawn must survive");
            Assert.That(unseeded.spawnEnemyCount, Is.EqualTo(3), "2 + floor(0.99 * 2) == 3");

            Assert.That(unseededRng.Draws, Is.EqualTo(seededRng.Draws + 1),
                "the guard suppresses the seed draw, it does not merely overwrite the result");
        }

        /// <summary>
        /// The trailing correction <c>if(tipEnemy == 5 || tipEnemy == 10) setKolEn(2, 1, 3, 1)</c>
        /// (<c>Land.as:1128-1131</c>) runs <i>after</i> the bands, so it replaces the band's slot-2
        /// value. Both runs below are the same level and the same script except for the draw that picks
        /// the tier, so the difference in slot 2 is entirely the correction.
        /// </summary>
        [Test]
        public void Apply_TipEnemyFiveOrTen_ReplacesSlotTwoWithTheCorrectionRange()
        {
            // biome 6, tier roll 0.1 -> tipEnemy 10 -> correction fires -> slot 2 == 1 + floor(0.99*3) == 3
            var corrected = new RoomDifficulty();
            LocationDifficultyPlanner.Apply(
                corrected,
                new LocationDifficultyInput(biome: 6, landDifLevel: 5f, difficultyOffset: 0f),
                LocationScriptedRng.Sequence(0.99f, 0.1f, 0.99f));

            Assert.That(corrected.tipEnemy, Is.EqualTo(10));
            Assert.That(corrected.GetEnemyCount(2), Is.EqualTo(3));

            // Same run, tier roll 0.5 -> tipEnemy 9 -> no correction -> slot 2 keeps the band's 2..5 (== 5)
            var uncorrected = new RoomDifficulty();
            LocationDifficultyPlanner.Apply(
                uncorrected,
                new LocationDifficultyInput(biome: 6, landDifLevel: 5f, difficultyOffset: 0f),
                LocationScriptedRng.Sequence(0.99f, 0.5f, 0.99f));

            Assert.That(uncorrected.tipEnemy, Is.EqualTo(9));
            Assert.That(uncorrected.GetEnemyCount(2), Is.EqualTo(5));
        }

        /// <summary>
        /// <c>if(act.biom == 11) setKolEn(2, 5, 8, 0)</c> is the last statement of <c>setLocDif</c>, so
        /// it wins over both the bands and the tier-5/10 correction.
        /// </summary>
        [Test]
        public void Apply_BiomeEleven_ForcesSlotTwoIntoTheHighBand()
        {
            var biomeEleven = Plan(landDifLevel: 5f, biome: 11);
            Assert.That(biomeEleven.tipEnemy, Is.EqualTo(11));
            Assert.That(biomeEleven.GetEnemyCount(2), Is.EqualTo(8));

            // Control: biome 4 keeps the band's slot-2 value at the same level.
            var control = Plan(landDifLevel: 5f, biome: 4);
            Assert.That(control.tipEnemy, Is.EqualTo(7));
            Assert.That(control.GetEnemyCount(2), Is.EqualTo(5));
        }

        /// <summary>
        /// <b>The regression test for the reported bug.</b> "Random rooms do not spawn enemies at all
        /// most of the time" was, exactly, <c>GetTotalEnemyCount() == 0</c> for every procedurally built
        /// room — because nothing ever called <c>SetEnemyCount</c>. This runs the planner over the nine
        /// real land rows with the real biome / config / base-difficulty values and asserts that a room
        /// on each of them ends up with enemies.
        ///
        /// <para>The first assertion is the control: a bare <see cref="RoomDifficulty"/> is still empty,
        /// so the test cannot pass by accident if <c>Apply</c> becomes a no-op.</para>
        /// </summary>
        [Test]
        public void Apply_ARealLandRow_GivesTheRoomANonZeroEnemyCount()
        {
            Assert.That(new RoomDifficulty().GetTotalEnemyCount(), Is.EqualTo(0),
                "control: an unplanned room has no enemies");

            // name, biome, config, base difficulty — read off the shipped land assets.
            var lands = new[]
            {
                ("random_plant", 0, 0, 0f),
                ("random_stable", 2, 1, 12f),
                ("random_sewer", 1, 2, 10f),
                ("random_mane", 3, 3, 15f),
                ("random_mbase", 4, 4, 22f),
                ("random_canter", 5, 5, 18f),
                ("random_encl", 6, 6, 27f),
                ("stable_pi", 10, 10, 0f),
                ("stable_pi_atk", 11, 11, 0f)
            };

            foreach ((string name, int biome, int config, float baseDifficulty) in lands)
            {
                var difficulty = new RoomDifficulty();
                LocationDifficultyPlanner.Apply(
                    difficulty,
                    new LocationDifficultyInput(
                        biome: biome,
                        landDifLevel: baseDifficulty,
                        difficultyOffset: 0f,
                        tipEnemy: -1,
                        landConfig: config),
                    new PcgRngService(0x5eedu).GetStream(RngStream.Spawn));

                Assert.That(difficulty.GetTotalEnemyCount(), Is.GreaterThan(0),
                    $"'{name}' (biome {biome}) must hold enemies after setLocDif");
                Assert.That(difficulty.GetTotalEnemyCount(), Is.LessThan(40),
                    $"'{name}' (biome {biome}) enemy count is implausible");
                Assert.That(difficulty.enemyLevel, Is.EqualTo(baseDifficulty).Within(1e-4),
                    $"'{name}' enemy level comes from the land, not the room's own lvl");
            }
        }

        /// <summary>A null difficulty or a null stream is a no-op rather than a throw.</summary>
        [Test]
        public void Apply_NullArguments_AreIgnored()
        {
            Assert.DoesNotThrow(() => LocationDifficultyPlanner.Apply(
                null,
                new LocationDifficultyInput(biome: 0, landDifLevel: 1f, difficultyOffset: 0f),
                LocationScriptedRng.Always(0.5f)));

            var difficulty = new RoomDifficulty();
            Assert.DoesNotThrow(() => LocationDifficultyPlanner.Apply(
                difficulty,
                new LocationDifficultyInput(biome: 0, landDifLevel: 1f, difficultyOffset: 0f),
                null));

            Assert.That(difficulty.GetTotalEnemyCount(), Is.EqualTo(0));
        }

        private static RoomDifficulty Plan(float landDifLevel, int biome = -1, int config = -1)
        {
            var difficulty = new RoomDifficulty();
            LocationDifficultyPlanner.Apply(
                difficulty,
                new LocationDifficultyInput(
                    biome: biome,
                    landDifLevel: landDifLevel,
                    difficultyOffset: 0f,
                    tipEnemy: -1,
                    landConfig: config),
                LocationScriptedRng.Always(0.99f));
            return difficulty;
        }

        private static int TipFor(int biome, float fallback, float landDifLevel = 0f)
        {
            var difficulty = new RoomDifficulty();
            LocationDifficultyPlanner.Apply(
                difficulty,
                new LocationDifficultyInput(
                    biome: biome,
                    landDifLevel: landDifLevel,
                    difficultyOffset: 0f,
                    tipEnemy: -1),
                LocationScriptedRng.Always(fallback));
            return difficulty.tipEnemy;
        }
    }

    /// <summary>
    /// Pins the <i>second</i> half of the random-enemy pipeline: the family that
    /// <see cref="LocationEnemySelector.RandomUnit"/> returns has to resolve to an id that actually names
    /// a shipped unit asset.
    ///
    /// <para><b>Why this fixture exists.</b> "randomUnit returned a family" is a different claim from "a
    /// unit spawned", and the audit that followed the empty-room fix found the gap between them. AS3
    /// <c>Unit.create</c> seeds each subclass from the DEFINITION row's <c>@cid</c> —
    /// <c>node = AllData.d.obj.(@id == id)[0]</c>, then <c>if(node.@cid.length()) cid = node.@cid</c>, and
    /// only then does the <c>randomCid()</c> result override it (<c>Unit.as:853-862</c>). On the
    /// random-enemy path the port passed no seed at all, so <c>trridge</c>/<c>trplate</c>/<c>trcans</c>/
    /// <c>trlaser</c> composed from a null seed and fell back to an id with no asset, while
    /// <c>landturret</c> silently resolved to the floor turret <c>turret0</c>. The first is a collider with
    /// no sprite; the second draws the wrong unit and therefore reads as working. Nothing covered
    /// either.</para>
    ///
    /// <para><b>The <c>definitionCid</c> column is real data.</b> It is transcribed from the shipped rows
    /// in <c>Resources/MapObjects/Definitions/*.asset</c> (<c>legacyAttributes</c>, key <c>cid</c>), which
    /// is the same field <c>Unit.create</c> reads out of <c>AllData</c>. A re-import that changed a cid is
    /// meant to fail these tests. A <c>null</c> cid means the row carries none, which is the common case
    /// and not an error: those families take their variant from <c>randomCid()</c> or stamp a constant
    /// id.</para>
    /// </summary>
    [TestFixture]
    public class RandomUnitOutputResolutionTests
    {
        /// <summary>
        /// The ids shipped in <c>Resources/Units</c>, transcribed from the asset directory. The invariant
        /// under test is "the resolved id is one of these", and nothing else here can check it: a
        /// <c>ScriptableObject</c> load is an ECall in the offline harness, so <c>Resources.LoadAll</c>
        /// would return an empty set and every assertion would pass vacuously.
        /// </summary>
        private static readonly HashSet<string> ShippedUnitIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "alicorn", "alicorn1", "alicorn2", "alicorn3", "ant", "ant1", "ant2", "ant3", "bigrobot",
            "bloat", "bloat0", "bloat1", "bloat10", "bloat2", "bloat3", "bloat4", "bloat5", "bloat6",
            "bloat7", "bloat8", "bloat9", "bloodwing", "bloodwing2", "bossalicorn", "bossdron",
            "bossencl", "bossnecr", "bossraider", "bossultra", "captive", "cryoslime", "damexpl1",
            "damgren", "damshot", "destr1", "doctor", "dron1", "dron2", "dron3", "dront", "eant",
            "ebloat", "encl", "encl1", "encl2", "encl3", "encl4", "eqd", "fish1", "fish2", "fish3",
            "gutsy", "gutsy1", "hellhound", "hellhound1", "littlepip", "merc", "merc1", "merc2",
            "merc3", "merc4", "merc5", "molerat", "monster", "moon", "msp", "mtrap", "mwall", "necros",
            "npc", "other", "owl", "phoenix", "pinkslime", "ponpon", "pony", "protect", "protect1",
            "raider", "raider1", "raider2", "raider3", "raider4", "raider5", "raider6", "raider7",
            "raider8", "raider9", "ranger", "ranger1", "ranger2", "ranger3", "rat", "robobrain",
            "robot", "roller", "roller2", "scorp1", "scorp2", "scorp3", "scythe", "sentinel", "slaver",
            "slaver1", "slaver2", "slaver3", "slaver4", "slaver5", "slaver6", "slime", "smallrobot",
            "spectre", "spritebot", "tarakan", "thunderhead", "training", "transmitter", "trigcans",
            "triglaser", "trigplate", "trigridge", "ttur", "turret", "turret0", "turret1", "turret2",
            "turret3", "turret4", "turret5", "vendor", "vortex", "zebra", "zebra1", "zebra2", "zebra3",
            "zebra4", "zebra5", "zombie", "zombie0", "zombie1", "zombie2", "zombie3", "zombie4",
            "zombie5", "zombie6", "zombie7", "zombie8", "zombie9",
        };

        /// <summary>
        /// How many distinct family ids <see cref="LocationEnemySelector.RandomUnit"/> can return.
        /// <c>enl2</c> 16 + <c>enl1</c> 10 + <c>enc1</c> 3 + <c>enf1</c> 4 + <c>lov</c> 7, with the
        /// duplicates (<c>fish</c> in three groups, <c>slime</c> in two) counted once.
        /// </summary>
        private const int RandomUnitFamilyCount = 40;

        /// <summary>
        /// One row per family <see cref="LocationEnemySelector.RandomUnit"/> can return that resolves to a
        /// shipped asset: the family, the definition row's <c>cid</c>, and the id the resolver must
        /// produce.
        ///
        /// <para>Grouped by the oracle arm that can produce the family, so a reader can check the table
        /// against <c>Location.as:1425-1726</c> arm by arm.</para>
        /// </summary>
        private static List<TestCaseData> FamilyTable()
        {
            var rows = new List<TestCaseData>();

            // ── enl2 (Location.as:1430-1542) ───────────────────────────────────────────────────────
            rows.Add(Case("stabpon", "stab", "ponpon"));      // UnitPonPon stamps its own id
            rows.Add(Case("zombie", null, "zombie0"));        // randomCid(level 0) -> "0"
            rows.Add(Case("eqd", null, "eqd"));               // no composition rule: the id is the id
            rows.Add(Case("landturret", "land", "turret1"));  // THE FIX: cid 'land' -> turret1
            rows.Add(Case("gutsy", null, "gutsy"));
            rows.Add(Case("protect", null, "protect"));
            rows.Add(Case("robot", null, "robot"));
            rows.Add(Case("slaver", null, "slaver1"));
            rows.Add(Case("merc", null, "merc1"));
            rows.Add(Case("alicorn", null, "alicorn1"));
            rows.Add(Case("zebra", null, "zebra1"));
            rows.Add(Case("ranger", null, "ranger1"));
            rows.Add(Case("necros", null, "necros"));
            rows.Add(Case("hellhound", null, "hellhound1"));
            rows.Add(Case("encl", null, "encl1"));
            rows.Add(Case("raider", null, "raider1"));

            // ── enl1 (Location.as:1543-1609) ───────────────────────────────────────────────────────
            rows.Add(Case("fish", null, "fish1"));
            rows.Add(Case("scorp3", "scorp3", "scorp3"));
            rows.Add(Case("slime", null, "slime"));
            rows.Add(Case("roller", null, "roller"));
            rows.Add(Case("msp", null, "msp"));
            rows.Add(Case("scorp", "scorp", "scorp1"));       // randomCid returns a WHOLE id here
            rows.Add(Case("ant", null, "ant1"));
            rows.Add(Case("rat", "rat", "rat"));
            rows.Add(Case("molerat", "molerat", "molerat"));
            rows.Add(Case("tarakan", "tarakan", "tarakan"));

            // ── enc1 (Location.as:1610-1647) ───────────────────────────────────────────────────────
            rows.Add(Case("turret", "floor", "turret0"));
            rows.Add(Case("bloodwing", null, "bloodwing"));
            rows.Add(Case("cturret", "combat", "turret0"));   // cid 'combat' -> tip 0, same as the null seed

            // ── enf1 (Location.as:1648-1682) ───────────────────────────────────────────────────────
            rows.Add(Case("bloat", null, "bloat0"));
            rows.Add(Case("vortex", null, "vortex"));
            rows.Add(Case("spritebot", null, "spritebot"));
            rows.Add(Case("dron", null, "dron1"));

            // ── lov (Location.as:1683-1723) ────────────────────────────────────────────────────────
            rows.Add(Case("slmine", null, "slime"));          // createUnit rewrites the family to "slime"
            rows.Add(Case("trap", null, "mtrap"));            // UnitTrap stamps its own id
            rows.Add(Case("trridge", "trigridge", "trigridge"));   // THE FIX: cid 'trigridge'
            rows.Add(Case("trplate", "trigplate", "trigplate"));   // THE FIX: cid 'trigplate'
            rows.Add(Case("trcans", "trigcans", "trigcans"));      // THE FIX: cid 'trigcans'
            rows.Add(Case("trlaser", "triglaser", "triglaser"));   // THE FIX: cid 'trlaser'

            return rows;
        }

        /// <summary>
        /// Families the audit found still resolve to an id with <b>no</b> shipped asset. Listed rather
        /// than omitted so the gap stays visible, and so that closing it (importing the assets) turns this
        /// fixture red instead of silently passing.
        /// </summary>
        private static List<TestCaseData> KnownGapTable()
        {
            var rows = new List<TestCaseData>();

            // UnitVariantResolver's own remarks record it: randomCid("mine") returns a whole id
            // ("mine"/"hmine"/"plamine"), and no Resources/Units/{mine,hmine,plamine}.asset exists — an
            // importer-side gap, not a resolver one. At level 0 the third roll declines and the answer is
            // "hmine".
            rows.Add(Case("mine", null, "hmine"));

            return rows;
        }

        private static TestCaseData Case(string family, string definitionCid, string expectedId) =>
            new TestCaseData(family, definitionCid, expectedId)
                .SetName($"{family}_{(definitionCid ?? "nocid")}_resolves_to_{expectedId}");

        /// <summary>
        /// The context the production random-enemy path builds: <see cref="LocationEnemyContext.NoContext"/>
        /// converted through <c>ToVariantContext()</c>. Deliberately not <c>default(UnitVariantContext)</c>
        /// — that has <c>LandY == 0</c>, a real land row, which would take <c>randomCid("ranger")</c>'s
        /// <c>LandY == 0</c> arm instead of rolling.
        /// </summary>
        private static UnitVariantContext RandomPathContext() =>
            LocationEnemyContext.NoContext.ToVariantContext();

        /// <summary>Resolve as the random-enemy path does, with a scripted stream fixed at 0.</summary>
        private static string Resolve(string family, string definitionCid) =>
            UnitVariantResolver.ResolveSpawnId(
                family,
                difficulty: 0f,
                rng: LocationScriptedRng.Always(0f),
                context: RandomPathContext(),
                placementTr: null,
                definitionCid: definitionCid);

        [TestCaseSource(nameof(FamilyTable))]
        public void EveryRandomUnitFamily_ResolvesToTheShippedId(string family, string definitionCid, string expectedId)
        {
            Assert.That(Resolve(family, definitionCid), Is.EqualTo(expectedId),
                $"'{family}' (definition cid {definitionCid ?? "<none>"}) must spawn '{expectedId}'");
        }

        /// <summary>
        /// The completeness control for the table above: every family
        /// <see cref="LocationEnemySelector.RandomUnit"/> can return is covered, exactly once.
        /// </summary>
        [Test]
        public void TheTables_CoverEveryRandomUnitFamilyExactlyOnce()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var duplicates = new List<string>();

            foreach (TestCaseData row in FamilyTable()) Add(row, seen, duplicates);
            foreach (TestCaseData row in KnownGapTable()) Add(row, seen, duplicates);

            CollectionAssert.IsEmpty(duplicates, "a family is listed twice");
            Assert.That(seen.Count, Is.EqualTo(RandomUnitFamilyCount),
                "the tables no longer cover every family randomUnit() can return — a new arm was added " +
                "to LocationEnemySelector without a row here");

            void Add(TestCaseData row, HashSet<string> into, List<string> dupes)
            {
                string family = (string)row.Arguments[0];
                if (!into.Add(family)) dupes.Add(family);
            }
        }

        /// <summary>
        /// Every id the table expects must name a real asset. This is the assertion that would have
        /// caught the pre-fix behaviour, where four <c>lov</c> families resolved to
        /// <c>trridge</c>/<c>trplate</c>/<c>trcans</c>/<c>trlaser</c> — ids with no
        /// <c>Resources/Units/*.asset</c> at all, i.e. an invisible collider.
        /// </summary>
        [Test]
        public void EveryResolvedFamilyId_NamesAShippedUnitAsset()
        {
            var missing = new List<string>();

            foreach (TestCaseData row in FamilyTable())
            {
                string family = (string)row.Arguments[0];
                string expectedId = (string)row.Arguments[2];
                if (!ShippedUnitIds.Contains(expectedId))
                {
                    missing.Add($"{family} -> {expectedId}");
                }
            }

            CollectionAssert.IsEmpty(missing,
                "these families resolve to an id with no Resources/Units asset (an invisible collider):");
        }

        /// <summary>
        /// The control that proves the fix's cid is load-bearing: <b>without</b> the definition row's
        /// <c>@cid</c> the four <c>lov</c> aliases and the land turret miss their asset. If someone
        /// removes the lookup from <c>RoomPopulator</c>, the behaviour returns to this and
        /// <see cref="EveryRandomUnitFamily_ResolvesToTheShippedId"/> goes red.
        /// </summary>
        [Test]
        public void WithoutTheDefinitionCid_TheAliasFamilies_MissTheirAsset()
        {
            foreach (string family in new[] { "trridge", "trplate", "trcans", "trlaser" })
            {
                string withoutCid = Resolve(family, null);

                // VerbatimCid declines on a null seed, so the resolver falls back to the placed id — which
                // is exactly the id that has no asset.
                Assert.That(withoutCid, Is.EqualTo(family), $"'{family}' with no cid falls back to itself");
                Assert.That(ShippedUnitIds.Contains(withoutCid), Is.False,
                    $"'{withoutCid}' must not be a shipped asset, or this control proves nothing");
            }

            // landturret is the nastier one: it keeps a valid-looking id and lands on the wrong mount.
            Assert.That(Resolve("landturret", null), Is.EqualTo("turret0"),
                "with no cid, TurretId()'s unknown-cid floor is the floor turret");
            Assert.That(Resolve("landturret", "land"), Is.EqualTo("turret1"),
                "cid 'land' is what makes it the LAND turret");
        }

        /// <summary>
        /// The gap's shape, pinned. Adding the assets is the fix; when that happens this test fails and
        /// forces the row to move into <see cref="FamilyTable"/>.
        /// </summary>
        [TestCaseSource(nameof(KnownGapTable))]
        public void KnownGap_ResolvesToAnIdWithNoAsset(string family, string definitionCid, string expectedId)
        {
            string resolved = Resolve(family, definitionCid);

            Assert.That(resolved, Is.EqualTo(expectedId),
                $"'{family}' now resolves to '{resolved}' — update the findings doc and this row");
            Assert.That(ShippedUnitIds.Contains(resolved), Is.False,
                $"'{resolved}' is now a shipped asset — the gap is closed; move '{family}' into FamilyTable");
        }

        /// <summary>
        /// The shipped definition rows carry the cids this table claims, for the families where the cid is
        /// what makes the id correct. Reads the assets as text because a <c>ScriptableObject</c> cannot be
        /// loaded offline; the path is relative to the project root, which is the harness's working
        /// directory.
        /// </summary>
        [TestCase("trridge", "trigridge")]
        [TestCase("trplate", "trigplate")]
        [TestCase("trcans", "trigcans")]
        [TestCase("trlaser", "triglaser")]
        [TestCase("landturret", "land")]
        [TestCase("cturret", "combat")]
        [TestCase("turret", "floor")]
        [TestCase("stabpon", "stab")]
        [TestCase("scorp", "scorp")]
        [TestCase("rat", "rat")]
        [TestCase("molerat", "molerat")]
        [TestCase("tarakan", "tarakan")]
        public void TheShippedDefinitionRow_CarriesTheExpectedCid(string objectId, string expectedCid)
        {
            string path = System.IO.Path.Combine(
                "Assets", "_PFE", "Data", "Resources", "MapObjects", "Definitions", objectId + ".asset");

            Assert.That(System.IO.File.Exists(path), Is.True,
                $"{path} is missing — the random-enemy path's cid lookup would find nothing for '{objectId}'");

            string[] lines = System.IO.File.ReadAllLines(path);
            string cid = null;
            for (int i = 0; i < lines.Length - 1; i++)
            {
                if (lines[i].Trim() == "- key: cid")
                {
                    cid = lines[i + 1].Trim().Replace("value:", string.Empty).Trim();
                    break;
                }
            }

            Assert.That(cid, Is.EqualTo(expectedCid),
                $"'{objectId}.asset' must carry cid '{expectedCid}'");
        }
    }
}
