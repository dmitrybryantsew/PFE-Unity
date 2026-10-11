using System.Collections.Generic;
using NUnit.Framework;
using PFE.Core.Rng;
using PFE.Systems.Map;

namespace PFE.Tests.Editor.Map
{
    /// <summary>
    /// Pins <see cref="UnitVariantResolver"/> — the port of <c>Location.randomCid()</c>
    /// (<c>Location.as:1728-1958</c>) plus the id composition each unit subclass does with its result
    /// (<c>UnitZombie.as:104</c> <c>id = "zombie" + this.tr</c>).
    ///
    /// <para><b>Why this needed pinning.</b> The port passed the room's authored id straight to the
    /// <c>UnitDefinition</c> lookup, so a room placing <c>zombie</c> spawned
    /// <c>Resources/Units/zombie.asset</c> — the content-free family template (one sprite, no
    /// <c>&lt;vis&gt;</c> art, no <c>&lt;comb hp&gt;</c>, no <c>&lt;move speed&gt;</c>). Every visible
    /// symptom of "the zombie is broken" descends from that one missing step.</para>
    ///
    /// <para><b>Half of these tests are about draw <i>counts</i>, not values.</b> The spawn stream is
    /// shared with the rest of room generation, so a branch that skips a draw the oracle takes
    /// desynchronises every later placement in the room — a bug whose only symptom is "the layout is
    /// different", with nothing to grep for. The counting RNG below is what makes that assertable.</para>
    /// </summary>
    [TestFixture]
    public class UnitVariantResolverTests
    {
        // ── Fixture ──────────────────────────────────────────────────────────

        /// <summary>
        /// A scripted <see cref="IRngService"/> that records which draws were taken and through which
        /// overload. An unconsumed script falls back to the range's <c>min</c>, which keeps a test that
        /// only cares about the draw count from having to script a value.
        /// </summary>
        private sealed class ScriptedRng : IRngService
        {
            private readonly Queue<int> _ints = new Queue<int>();
            private readonly Queue<float> _floats = new Queue<float>();

            public ScriptedRng(int[] ints = null, float[] floats = null)
            {
                if (ints != null) { foreach (int v in ints) _ints.Enqueue(v); }
                if (floats != null) { foreach (float v in floats) _floats.Enqueue(v); }
            }

            /// <summary>Draws taken through the integer <c>Range</c> — every <c>Math.floor(random*n+b)</c>.</summary>
            public int IntDraws { get; private set; }

            /// <summary>Draws taken through <see cref="NextFloat"/> — every probabilistic test.</summary>
            public int FloatDraws { get; private set; }

            public int TotalDraws => IntDraws + FloatDraws;

            /// <summary>The last integer range asked for, so a test can assert the <i>bounds</i> too.</summary>
            public int LastMinInclusive { get; private set; } = int.MinValue;

            public int LastMaxExclusive { get; private set; } = int.MinValue;

            public int Range(int minInclusive, int maxExclusive)
            {
                IntDraws++;
                LastMinInclusive = minInclusive;
                LastMaxExclusive = maxExclusive;
                return _ints.Count > 0 ? _ints.Dequeue() : minInclusive;
            }

            public float NextFloat()
            {
                FloatDraws++;
                return _floats.Count > 0 ? _floats.Dequeue() : 0f;
            }

            public IRngService GetStream(RngStream stream, int? salt = null) => this;

            public uint NextUInt() => 0u;

            public int NextInt(int maxExclusive) => Range(0, maxExclusive);

            public float Range(float min, float max) => min;

            public bool Chance(float probability) => NextFloat() < probability;

            public void Shuffle<T>(IList<T> list) { }
        }

        // ── The pinned bug ───────────────────────────────────────────────────

        /// <summary>
        /// The camp room places <c>zombie</c> at difficulty 0, and the oracle's ladder ends in a bare
        /// <c>_loc2_ = 0</c> for <c>locDifLevel &lt; 2</c> — no draw at all. So the only correct answer
        /// is <c>zombie0</c>, deterministically, whatever the seed.
        /// </summary>
        [Test]
        public void Zombie_AtDifficultyZero_ResolvesToVariantZero_WithoutDrawing()
        {
            var rng = new ScriptedRng();

            string cid = UnitVariantResolver.RandomCid("zombie", 0f, rng);
            Assert.AreEqual("0", cid, "Location.as:1866-1869 — the `else` arm is a bare 0.");
            Assert.AreEqual(0, rng.TotalDraws,
                "That arm takes no draw, and the spawn stream is shared — a draw here shifts every " +
                "later placement in the room.");

            string resolved = UnitVariantResolver.ResolveSpawnId("zombie", 0f, rng);
            Assert.AreEqual("zombie0", resolved,
                "UnitZombie.as:104 — `id = \"zombie\" + this.tr`, so cid \"0\" is zombie0.");
            Assert.AreNotEqual("zombie", resolved,
                "The family id is the content-free template (1 sprite, no art, no stats) — returning it " +
                "is the bug this class exists to fix.");
        }

        /// <summary>
        /// A difficulty above the ladder's first step must reach a <i>different</i> arm, or the ladder is
        /// decorative. <c>d &gt;= 2</c> is <c>Math.floor(random * 4)</c>, i.e. <c>Range(0, 4)</c>.
        /// </summary>
        [Test]
        public void Zombie_DifficultyLadder_UsesTheOraclesBoundsAtEachStep()
        {
            // d >= 2 -> Math.floor(random * 4)
            var atTwo = new ScriptedRng();
            UnitVariantResolver.RandomCid("zombie", 2f, atTwo);
            Assert.AreEqual(0, atTwo.LastMinInclusive);
            Assert.AreEqual(4, atTwo.LastMaxExclusive, "Location.as:1860 — `random() * 4`.");
            Assert.AreEqual(1, atTwo.IntDraws);

            // d >= 5 -> Math.floor(random * 5)
            var atFive = new ScriptedRng();
            UnitVariantResolver.RandomCid("zombie", 5f, atFive);
            Assert.AreEqual(5, atFive.LastMaxExclusive, "Location.as:1856 — `random() * 5`.");

            // d >= 8 with a biome -> Math.floor(random * 7). NoBiome must NOT take this arm.
            var atEightWithBiome = new ScriptedRng();
            UnitVariantResolver.RandomCid(
                "zombie", 8f, atEightWithBiome, new UnitVariantContext(biome: 3));
            Assert.AreEqual(7, atEightWithBiome.LastMaxExclusive,
                "Location.as:1850-1853 — the `biome >= 1 && locDifLevel >= 8` arm.");

            var atEightNoBiome = new ScriptedRng();
            UnitVariantResolver.RandomCid("zombie", 8f, atEightNoBiome, UnitVariantResolver.NoContext);
            Assert.AreEqual(5, atEightNoBiome.LastMaxExclusive,
                "With no biome the oracle falls through to the `locDifLevel >= 5` arm, not the 8 one. " +
                "NoBiome is negative so that `biome >= 1` is false — this is the control for it.");
        }

        /// <summary>
        /// Biome 5 is the one arm with a <b>second</b> draw inside it, taken before the tier roll. Getting
        /// the order wrong swaps a 10 % chance of variant 9 with a 10 % chance of shifting every later
        /// placement.
        /// </summary>
        [Test]
        public void Zombie_Biome5_TakesTheTierRollAndTheTenthChanceInTheOraclesOrder()
        {
            // First float 0.05 (< 0.1) -> variant 9, and the tier roll is NOT taken.
            var jackpot = new ScriptedRng(floats: new[] { 0.05f });
            Assert.AreEqual("9", UnitVariantResolver.RandomCid(
                "zombie", 20f, jackpot, new UnitVariantContext(biome: 5)));
            Assert.AreEqual(1, jackpot.FloatDraws);
            Assert.AreEqual(0, jackpot.IntDraws,
                "Location.as:1844-1848 — the 0.1 test is the `if`; the tier roll is the `else`.");

            // First float 0.5 (>= 0.1) -> falls to the tier roll, Math.floor(random * 4 + 5).
            var normal = new ScriptedRng(ints: new[] { 7 }, floats: new[] { 0.5f });
            Assert.AreEqual("7", UnitVariantResolver.RandomCid(
                "zombie", 20f, normal, new UnitVariantContext(biome: 5)));
            Assert.AreEqual(1, normal.FloatDraws);
            Assert.AreEqual(1, normal.IntDraws);
            Assert.AreEqual(5, normal.LastMinInclusive, "Location.as:1852 — `random() * 4 + 5`.");
            Assert.AreEqual(9, normal.LastMaxExclusive);
        }

        // ── The composition half ─────────────────────────────────────────────

        /// <summary>
        /// <c>UnitZombie.as:83-101</c> reads the placement node's <c>tr</c> before the cid, so an authored
        /// <c>tr</c> must win over the roll — but it does <b>not</b> suppress the roll.
        ///
        /// <para><b>This test used to assert the opposite</b> ("An authored tr means randomCid is never
        /// called"), and that was wrong. <c>Location.as:1021</c> — the placement arm — passes no
        /// <c>ncid</c>, so <c>createUnit</c> takes its <c>else</c> at <c>:1169/:1185</c> and calls
        /// <c>randomCid()</c> unconditionally; the <c>@tr</c> branch inside the subclass merely
        /// <i>discards</i> the result. 472 shipped placements author <c>@tr</c>, so the old behaviour
        /// desynchronised the spawn stream at every one of them.</para>
        /// </summary>
        [Test]
        public void PlacementTr_OverridesTheRolledCid_ButStillTakesTheRoll()
        {
            // d >= 2 is `Math.floor(random * 4)` — one draw, taken before the subclass looks at @tr.
            var rng = new ScriptedRng();

            string resolved = UnitVariantResolver.ResolveSpawnId(
                "zombie", 2f, rng, UnitVariantResolver.NoContext, placementTr: "3");

            Assert.AreEqual("zombie3", resolved, "The authored tr outranks the rolled cid.");
            Assert.AreEqual(1, rng.IntDraws,
                "The roll is taken and discarded, not skipped: Location.as:1021 passes no ncid, so " +
                "randomCid() runs regardless of the placement's @tr. Skipping it shifts every later " +
                "placement in the room.");
        }

        /// <summary>
        /// The cid <b>seed</b> comes off the definition row, not the placement
        /// (<c>Unit.as:853-862</c>), and the alias families map it onto their own id. A room places
        /// <c>trplate</c>; the definition row says <c>cid='trigplate'</c>; the spawned id is
        /// <c>trigplate</c>. Before this was plumbed, <c>ResolveSpawnId</c> returned the placed id and the
        /// unit resolved to no <c>UnitDefinition</c> at all.
        /// </summary>
        [Test]
        public void DefinitionCid_IsTheSeedForTheAliasFamilies()
        {
            Assert.AreEqual("trigplate", UnitVariantResolver.ResolveSpawnId(
                "trplate", 0f, new ScriptedRng(), UnitVariantResolver.NoContext,
                placementTr: null, definitionCid: "trigplate"));

            Assert.AreEqual("turret0", UnitVariantResolver.ResolveSpawnId(
                "turret", 0f, new ScriptedRng(), UnitVariantResolver.NoContext,
                placementTr: null, definitionCid: "floor"));

            Assert.AreEqual("turret2", UnitVariantResolver.ResolveSpawnId(
                "wturret", 0f, new ScriptedRng(), UnitVariantResolver.NoContext,
                placementTr: null, definitionCid: "wall"));

            Assert.AreEqual("ponpon", UnitVariantResolver.ResolveSpawnId(
                "zebpon", 0f, new ScriptedRng(), UnitVariantResolver.NoContext,
                placementTr: null, definitionCid: "zebra"));
        }

        /// <summary>
        /// The scorpion defect, end to end. <c>scorp.asset</c>'s definition row carries
        /// <c>cid='scorp'</c>, and <c>randomCid("scorp")</c> overrides that seed with a <b>whole id</b>
        /// (<c>"scorp" + n</c>). 45 of the 639 shipped rooms place <c>scorp</c>, and with no composition
        /// rule the resolver returned <c>"scorp"</c> — an id with no <c>Resources/Units/scorp.asset</c>,
        /// so the scorpion drew nothing and read <c>UnitDefinition.DefaultHealth</c>.
        /// </summary>
        [Test]
        public void Scorp_ResolvesToARealVariantAsset_NotTheBareFamilyId()
        {
            var rng = new ScriptedRng(ints: new[] { 2 });

            string resolved = UnitVariantResolver.ResolveSpawnId(
                "scorp", 5f, rng, UnitVariantResolver.NoContext,
                placementTr: null, definitionCid: "scorp");

            Assert.AreEqual("scorp2", resolved,
                "The roll overrides the definition's `cid='scorp'` seed; UnitMonstrik.as:22-29 uses the " +
                "cid verbatim, so the composition step is the identity.");
            Assert.AreNotEqual("scorp", resolved,
                "`scorp` is the seed, not an asset — Units/scorp.asset does not exist.");
        }

        /// <summary>
        /// <c>UnitRoller.as:24-45</c> appends the number only when <c>tr &gt;= 2</c>, which is why the
        /// asset directory holds <c>roller</c> and <c>roller2</c> but no <c>roller1</c>. Composing
        /// <c>"roller" + 1</c> would produce an id that resolves to nothing.
        /// </summary>
        [Test]
        public void Roller_TrOne_YieldsTheBareFamilyId_NotRoller1()
        {
            Assert.AreEqual("roller", UnitVariantResolver.ResolveSpawnId(
                "roller", 0f, new ScriptedRng(), UnitVariantResolver.NoContext));
            Assert.AreEqual("roller2", UnitVariantResolver.ResolveSpawnId(
                "roller", 0f, new ScriptedRng(), UnitVariantResolver.NoContext, placementTr: "2"));
        }

        /// <summary>
        /// A family with no composition rule passes through untouched — correct for the subclasses that
        /// ignore the cid outright (<c>UnitTrain.as:13</c> <c>id = "training"</c>), and the documented
        /// boundary of what has been read against the oracle.
        /// </summary>
        [Test]
        public void FamilyWithoutACompositionRule_PassesThroughUnchanged()
        {
            var rng = new ScriptedRng();
            Assert.AreEqual("training", UnitVariantResolver.ResolveSpawnId("training", 0f, rng));
            Assert.AreEqual(0, rng.TotalDraws,
                "randomCid's `default:` returns null; it must not be reached by a non-family id, and " +
                "the pass-through must not draw either.");
        }

        /// <summary>
        /// <c>scorp</c> is the one family whose <c>randomCid</c> arm returns a <b>whole id</b>
        /// (<c>"scorp" + n</c>) rather than a bare number, so its composition rule is the identity —
        /// see <see cref="Scorp_ResolvesToARealVariantAsset_NotTheBareFamilyId"/> for the end-to-end half.
        /// </summary>
        [Test]
        public void Scorp_ReturnsAWholeIdFromRandomCid()
        {
            var rng = new ScriptedRng(ints: new[] { 2 });
            Assert.AreEqual("scorp2", UnitVariantResolver.RandomCid("scorp", 5f, rng));
            Assert.AreEqual(1, rng.IntDraws);
        }

        // ── The table/switch invariant ───────────────────────────────────────

        /// <summary>
        /// Every family <see cref="UnitVariantResolver.RandomCid"/> has a <c>case</c> for must have an
        /// entry in the composition table, or <c>ResolveSpawnId</c> returns the raw <i>variant number</i>
        /// ("2") where an id is expected ("pinkslime").
        ///
        /// <para><b>The case list is read out of the source, not hand-written.</b> A hand-listed set is
        /// exactly how the original gap stayed invisible: it can only ever agree with itself, so a new
        /// <c>case</c> added to the switch is not covered by it. This parses the switch out of
        /// <c>UnitVariantResolver.cs</c> itself, so the two cannot drift apart, and the parse is
        /// controlled by asserting it found a plausible number of cases.</para>
        /// </summary>
        [Test]
        public void EveryRandomCidCase_HasACompositionRule()
        {
            var cases = ReadRandomCidCaseLabels();
            Assert.Greater(cases.Count, 15,
                "Positive control: the switch parse found too few cases, so this test is not actually " +
                "reading the switch — fix the parser before trusting a green result.");

            var rules = ReadCompositionRuleKeys();

            var missing = new List<string>();
            foreach (string family in cases)
            {
                if (!rules.Contains(family))
                {
                    missing.Add(family);
                }
            }

            CollectionAssert.IsEmpty(missing,
                "RandomCid has a case for these families but no composition rule, so ResolveSpawnId " +
                "would return a bare variant number as if it were a unit id: " +
                string.Join(", ", missing));
        }

        static List<string> ReadRandomCidCaseLabels()
        {
            string path = FindResolverSourcePath();
            Assert.IsNotNull(path,
                "Cannot locate Assets/_PFE/Systems/Map/UnitVariantResolver.cs — this guard reads the " +
                "source, so it cannot run without it.");

            string[] lines = System.IO.File.ReadAllLines(path);

            // Locate RandomCid's body, then collect `case "x":` labels until the switch closes.
            int start = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("public static string RandomCid("))
                {
                    start = i;
                    break;
                }
            }

            Assert.GreaterOrEqual(start, 0, "RandomCid declaration not found in the source.");

            var labels = new List<string>();
            int depth = 0;
            bool inSwitch = false;
            for (int i = start; i < lines.Length; i++)
            {
                string line = lines[i];
                if (!inSwitch && line.Contains("switch ("))
                {
                    inSwitch = true;
                }

                if (!inSwitch)
                {
                    continue;
                }

                foreach (string token in SplitCaseTokens(line))
                {
                    labels.Add(token);
                }

                depth += CountOf(line, '{') - CountOf(line, '}');
                if (depth <= 0 && labels.Count > 0 && i > start)
                {
                    break;
                }
            }

            return labels;
        }

        /// <summary>
        /// Locates <c>UnitVariantResolver.cs</c> without depending on Unity, so this guard is runnable in
        /// the offline verification harness as well as the editor.
        ///
        /// <para><c>Application.dataPath</c> is the obvious answer but it is an ECall: outside the editor
        /// it throws <c>SecurityException</c>, which would make the whole guard un-runnable and therefore
        /// worthless. So it is tried first and swallowed, then the file is found by walking up from the
        /// working directory and the assembly location — both of which sit inside the project when the
        /// harness runs.</para>
        /// </summary>
        static string FindResolverSourcePath()
        {
            string relative = System.IO.Path.Combine(
                "_PFE", "Systems", "Map", "UnitVariantResolver.cs");

            try
            {
                string dataPath = UnityEngine.Application.dataPath;
                if (!string.IsNullOrEmpty(dataPath))
                {
                    string candidate = System.IO.Path.Combine(dataPath, relative);
                    if (System.IO.File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch (System.Exception)
            {
                // Offline harness — fall through to the filesystem walk.
            }

            string[] roots =
            {
                System.IO.Directory.GetCurrentDirectory(),
                System.AppContext.BaseDirectory
            };

            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root))
                {
                    continue;
                }

                var dir = new System.IO.DirectoryInfo(root);
                for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
                {
                    string candidate = System.IO.Path.Combine(dir.FullName, "Assets", relative);
                    if (System.IO.File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        /// <summary>Pulls the <c>"name"</c> out of every <c>case "name":</c> on a line.</summary>
        static IEnumerable<string> SplitCaseTokens(string line)
        {
            int index = 0;
            while (true)
            {
                int at = line.IndexOf("case \"", index, System.StringComparison.Ordinal);
                if (at < 0)
                {
                    yield break;
                }

                int open = at + 6; // past `case "` — six characters, not five (the quote is the sixth)
                int close = line.IndexOf('"', open);
                if (close < 0)
                {
                    yield break;
                }

                yield return line.Substring(open, close - open);
                index = close + 1;
            }
        }

        static int CountOf(string text, char c)
        {
            int n = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == c)
                {
                    n++;
                }
            }

            return n;
        }

        static List<string> ReadCompositionRuleKeys()
        {
            var field = typeof(UnitVariantResolver).GetField(
                "CompositionRules",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(field, "CompositionRules not found — has it been renamed?");

            var dict = field.GetValue(null) as System.Collections.IDictionary;
            Assert.IsNotNull(dict, "CompositionRules is not an IDictionary — reflection path is stale.");

            var keys = new List<string>();
            foreach (object key in dict.Keys)
            {
                if (key is string s)
                {
                    keys.Add(s);
                }
            }

            Assert.Greater(keys.Count, 15,
                "Positive control: too few composition rules were read, so the reflection path is wrong.");
            return keys;
        }

        // ── The default-struct trap ──────────────────────────────────────────

        /// <summary>
        /// <b>A negative control for a real hazard.</b> <c>default(UnitVariantContext)</c> zeroes
        /// <c>LandY</c>, and <c>LandY == 0</c> is a genuine land index — so a defaulted context makes
        /// <c>ranger</c> take the <c>landY == 0</c> arm and return <c>"1"</c> without drawing, where the
        /// oracle would roll. <see cref="UnitVariantResolver.NoContext"/> is the value to pass instead.
        ///
        /// <para>This test exists so that "simplify <c>NoContext</c> back to <c>default</c>" fails
        /// loudly rather than silently turning every ranger in the game into variant 1.</para>
        /// </summary>
        [Test]
        public void DefaultContext_IsNotNoContext_BecauseLandYZeroIsARealLand()
        {
            Assert.AreEqual(-1, UnitVariantResolver.NoContext.LandY);

            var rolled = new ScriptedRng();
            Assert.AreEqual("1", UnitVariantResolver.RandomCid(
                "ranger", 0f, rolled, UnitVariantResolver.NoContext));
            Assert.AreEqual(1, rolled.IntDraws,
                "With no context the oracle rolls `random() * 2 + 1`.");
            Assert.AreEqual(1, rolled.LastMinInclusive);
            Assert.AreEqual(3, rolled.LastMaxExclusive);

            var defaulted = new ScriptedRng();
            Assert.AreEqual("1", UnitVariantResolver.RandomCid("ranger", 0f, defaulted, default));
            Assert.AreEqual(0, defaulted.TotalDraws,
                "The trap: a defaulted struct has LandY == 0, so the `landY == 0` arm is taken and the " +
                "roll is skipped. Use UnitVariantResolver.NoContext.");
        }
    }
}
