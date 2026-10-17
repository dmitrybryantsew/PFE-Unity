using NUnit.Framework;
using PFE.Systems.Map;
using PFE.Tests.Editor.Map;

namespace PFE.Tests.Editor.Campaign
{
    /// <summary>
    /// D4 — <c>globalDif</c> inside <c>Land.setLocDif</c> (<c>Land.as:988-995</c>), the one place the
    /// difficulty SETTING changes a room.
    ///
    /// <para><b>What is under test.</b> Two statements, inserted after <c>enemyLevel = locDifLevel</c>
    /// (<c>:987</c>) and <b>before</b> the <c>tipEnemy</c> block (<c>:996</c>):</para>
    /// <code>
    /// if (globalDif &lt; 2) difficulty.earMult   *= 0.5f;
    /// if (globalDif &gt; 2) difficulty.enemyLevel += (globalDif - 2) * 2;
    /// </code>
    ///
    /// <para><b>Why the fixture lives here rather than in the planner's own file.</b> The existing
    /// <c>LocationDifficultyPlannerTests</c> pin the draw counts and the count bands, and they must keep
    /// passing <i>unchanged</i> — that is this item's real acceptance check. The two new statements draw
    /// nothing, so every draw count and every band must be identical at every setting; the
    /// <see cref="Apply_TheTwoGlobalDifStatementsDrawNothing"/> test states that as an equality rather
    /// than leaving it to be re-discovered.</para>
    /// </summary>
    [TestFixture]
    public class DifficultyPlannerGlobalDifTests
    {
        /// <summary>Runs <c>Apply</c> once on a fresh record and returns it, with the RNG kept so the
        /// draw count can be asserted.</summary>
        private static RoomDifficulty Apply(int globalDif, out LocationScriptedRng rng,
                                            float landDifLevel = 5f, int biome = -1, float draw = 0.99f)
        {
            var difficulty = new RoomDifficulty();
            rng = LocationScriptedRng.Always(draw);
            LocationDifficultyPlanner.Apply(
                difficulty,
                new LocationDifficultyInput(biome, landDifLevel, difficultyOffset: 0f, globalDifficulty: globalDif),
                rng);
            return difficulty;
        }

        private static RoomDifficulty Apply(int globalDif, float landDifLevel = 5f, int biome = -1,
                                           float draw = 0.99f)
        {
            return Apply(globalDif, out _, landDifLevel, biome, draw);
        }

        /// <summary>
        /// Easy and Very-easy: <c>earMult *= 0.5</c> and <b>nothing else</b> — the enemy level is
        /// untouched, so a room's difficulty is the same and only hearing shrinks.
        /// </summary>
        [Test]
        public void Apply_Easy_HalvesEarMultAndLeavesTheLevelAlone()
        {
            RoomDifficulty easy = Apply(globalDif: 0);
            RoomDifficulty veryEasy = Apply(globalDif: 1);

            Assert.That(easy.earMult, Is.EqualTo(0.5f).Within(1e-5), "Land.as:988-991");
            Assert.That(veryEasy.earMult, Is.EqualTo(0.5f).Within(1e-5));
            Assert.That(easy.enemyLevel, Is.EqualTo(5f).Within(1e-4), "the level is NOT reduced below Normal");
            Assert.That(veryEasy.enemyLevel, Is.EqualTo(5f).Within(1e-4));
        }

        /// <summary>Normal is the identity for both statements — 2 is where the oracle's default sits.</summary>
        [Test]
        public void Apply_Normal_IsTheIdentity()
        {
            RoomDifficulty normal = Apply(globalDif: 2);

            Assert.That(normal.earMult, Is.EqualTo(1f).Within(1e-5));
            Assert.That(normal.enemyLevel, Is.EqualTo(5f).Within(1e-4));
        }

        /// <summary>
        /// Above Normal, <c>enemyLevel += (globalDif - 2) * 2</c>: Hard raises it by 2, Super-hard by 4.
        /// Note the guard is <c>&gt; 2</c>, strict — Normal itself adds nothing.
        /// </summary>
        [Test]
        public void Apply_AboveNormal_RaisesEnemyLevelByTwoPerStep()
        {
            Assert.That(Apply(globalDif: 3).enemyLevel, Is.EqualTo(7f).Within(1e-4));
            Assert.That(Apply(globalDif: 4).enemyLevel, Is.EqualTo(9f).Within(1e-4),
                "Super-hard is `+ (4 - 2) * 2` = +4, not +2");

            Assert.That(Apply(globalDif: 2).enemyLevel, Is.EqualTo(5f).Within(1e-4),
                "the guard is strict `> 2`, so Normal adds nothing");
        }

        /// <summary>
        /// <b>Only <c>enemyLevel</c> moves.</b> The oracle's block does not touch
        /// <c>weaponLevel</c>/<c>lockLevel</c>/<c>mechLevel</c>/<c>baseDifficulty</c> — so on Super-hard
        /// the enemies are four levels harder while the loot they carry is still gated by the land's own
        /// level. A port that raised <c>weaponLevel</c> too would hand out better guns on hard mode.
        /// </summary>
        [Test]
        public void Apply_AboveNormal_LeavesTheOtherLevelFieldsAlone()
        {
            RoomDifficulty easy = Apply(globalDif: 0, landDifLevel: 8f);
            RoomDifficulty hard = Apply(globalDif: 4, landDifLevel: 8f);

            Assert.That(hard.baseDifficulty, Is.EqualTo(easy.baseDifficulty).Within(1e-4));
            Assert.That(hard.lockLevel, Is.EqualTo(easy.lockLevel).Within(1e-4));
            Assert.That(hard.mechLevel, Is.EqualTo(easy.mechLevel).Within(1e-4));
            Assert.That(hard.weaponLevel, Is.EqualTo(easy.weaponLevel).Within(1e-4),
                "weaponLevel must NOT scale with the setting — only enemyLevel does");
            Assert.That(hard.weaponLevel, Is.EqualTo(3f).Within(1e-4), "1 + 8/4");

            Assert.That(hard.enemyLevel, Is.EqualTo(easy.enemyLevel + 4f).Within(1e-4));
        }

        /// <summary>
        /// <b>The acceptance check for D4: the two statements draw nothing.</b> The spawn stream is shared
        /// with room layout, so one extra draw per room would re-roll every later placement in the land
        /// with no error. Every setting must consume exactly the same number of draws.
        /// </summary>
        [Test]
        public void Apply_TheTwoGlobalDifStatementsDrawNothing()
        {
            int baseline = -1;
            for (int globalDif = 0; globalDif <= 4; globalDif++)
            {
                RoomDifficulty difficulty = Apply(globalDif, out LocationScriptedRng rng);
                Assert.That(rng.Draws, Is.GreaterThan(0),
                    "control: the run must actually draw, or this comparison proves nothing");

                if (baseline < 0) baseline = rng.Draws;
                Assert.That(rng.Draws, Is.EqualTo(baseline),
                    "globalDif = {0} must spend exactly the same draws as globalDif = 0", globalDif);
            }
        }

        /// <summary>
        /// Same draw-parity check in the other count band (<c>locDifLevel &lt; 4</c>) and at a
        /// <c>tipEnemy</c>-rolling biome, because the two statements sit directly in front of the
        /// <c>tipEnemy</c> block and that is where an off-by-one draw would hide.
        /// </summary>
        [Test]
        public void Apply_DrawParity_HoldsInTheLowBandAndAtARollingBiome()
        {
            foreach ((float landDifLevel, int biome) in new[] { (1f, -1), (5f, 0), (13f, 3) })
            {
                int baseline = -1;
                for (int globalDif = 0; globalDif <= 4; globalDif++)
                {
                    Apply(globalDif, out LocationScriptedRng rng, landDifLevel, biome, 0.99f);
                    if (baseline < 0) baseline = rng.Draws;
                    Assert.That(rng.Draws, Is.EqualTo(baseline),
                        "landDifLevel {0}, biome {1}, globalDif {2}", landDifLevel, biome, globalDif);
                }
            }
        }

        /// <summary>
        /// <c>earMult *= 0.5</c> is a MULTIPLY on the record's own default of 1
        /// (<c>Location.as:259</c>), so it is <b>not idempotent</b>: a second <c>Apply</c> on the same
        /// record gives 0.25. Recorded because <c>setLocDif</c> runs once per constructed room
        /// (<c>Land.as:975</c>) and a second call — from a room rebuild, say — would quietly halve
        /// hearing again.
        /// </summary>
        [Test]
        public void Apply_EarMultIsAMultiply_SoItIsNotIdempotent()
        {
            Assert.That(new RoomDifficulty().earMult, Is.EqualTo(1f).Within(1e-5),
                "the record's default is the oracle's `Location.earMult = 1`");

            var difficulty = new RoomDifficulty();
            var input = new LocationDifficultyInput(-1, 5f, 0f, globalDifficulty: 0);

            LocationDifficultyPlanner.Apply(difficulty, input, LocationScriptedRng.Always(0.99f));
            Assert.That(difficulty.earMult, Is.EqualTo(0.5f).Within(1e-5));

            LocationDifficultyPlanner.Apply(difficulty, input, LocationScriptedRng.Always(0.99f));
            Assert.That(difficulty.earMult, Is.EqualTo(0.25f).Within(1e-5),
                "a second Apply halves again — call this once per room, at build time");
        }

        /// <summary>
        /// The default input carries <c>globalDifficulty: 2</c> (Normal) so a caller that never mentions
        /// the setting gets the identity rather than a silent halving — a default of 0 would halve
        /// <c>earMult</c> in every room built before the setting is seeded.
        /// </summary>
        [Test]
        public void Input_DefaultsToNormal()
        {
            var input = new LocationDifficultyInput(biome: -1, landDifLevel: 5f, difficultyOffset: 0f);

            Assert.That(input.GlobalDifficulty, Is.EqualTo(2));
            Assert.That(Apply(globalDif: input.GlobalDifficulty).earMult, Is.EqualTo(1f).Within(1e-5));
        }
    }
}
