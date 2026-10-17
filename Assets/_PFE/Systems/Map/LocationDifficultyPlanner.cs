using PFE.Core.Rng;

namespace PFE.Systems.Map
{
    /// <summary>
    /// The land-level inputs AS3 <c>Land.setLocDif()</c> reads. Everything here comes from the
    /// <c>&lt;land&gt;</c> row (<c>LandAct</c>) or from the room's position in the land grid — none of it
    /// is per-room authored data.
    /// </summary>
    public readonly struct LocationDifficultyInput
    {
        /// <summary>AS3 <c>LandAct.biom</c> — the land's theme. Drives most of <c>tipEnemy</c>.</summary>
        public readonly int Biome;

        /// <summary>
        /// AS3 <c>Land.landDifLevel</c> — <c>max(param3, act.dif)</c>, where <c>act.dif</c> is the land's
        /// authored difficulty and <c>param3</c> is the run difficulty.
        /// </summary>
        public readonly float LandDifLevel;

        /// <summary>
        /// AS3 <c>Land.newLoc</c>'s <c>param2</c> — the depth bonus: <c>y/2</c> (conf 0), <c>y</c>
        /// (conf 1), <c>y*2.5</c> (conf 2), else <c>0</c>. See
        /// <c>LandLayoutPlanner.DifficultyOffsetFor</c>.
        /// </summary>
        public readonly float DifficultyOffset;

        /// <summary>AS3 <c>Location.tipEnemy</c> as set by the room's <c>entip</c> option, or <c>-1</c>.</summary>
        public readonly int TipEnemy;

        /// <summary>AS3 <c>LandAct.conf</c>.</summary>
        public readonly int LandConfig;

        /// <summary>
        /// AS3 <c>World.w.game.globalDif</c> — the run-wide difficulty SETTING (<c>Game.as:41</c>), read
        /// by <c>setLocDif</c> at <c>Land.as:988-995</c>.
        ///
        /// <para><b>Defaults to 2, and the default is load-bearing.</b> 2 is the oracle's own default and
        /// the identity for both of the adjustments, so a caller that does not pass it gets the behaviour
        /// every pre-existing fixture was written against. A default of 0 would silently halve
        /// <c>earMult</c> in every room.</para>
        /// </summary>
        public readonly int GlobalDifficulty;

        public LocationDifficultyInput(
            int biome,
            float landDifLevel,
            float difficultyOffset,
            int tipEnemy = -1,
            int landConfig = -1,
            int globalDifficulty = 2)
        {
            Biome = biome;
            LandDifLevel = landDifLevel;
            DifficultyOffset = difficultyOffset;
            TipEnemy = tipEnemy;
            LandConfig = landConfig;
            GlobalDifficulty = globalDifficulty;
        }
    }

    /// <summary>
    /// Port of AS3 <c>Land.setLocDif(param1:Location, param2:Number)</c>
    /// (<c>Land.as:980-1136</c>) — the one place the oracle decides a room's difficulty level, its
    /// <c>tipEnemy</c>, and <b>how many enemies it holds</b>.
    ///
    /// <para><b>Why this had to exist.</b> <see cref="RoomDifficulty.SetEnemyCount"/> is the only writer
    /// of <c>enemyCounts</c>, and it had no production caller at all — so every room's
    /// <c>GetTotalEnemyCount()</c> was <c>0</c>, the random-enemy pass placed nothing, and
    /// <c>room.difficulty.enemyLevel</c> was being filled from the room's authored <c>lvl</c> instead of
    /// the land's difficulty. Both symptoms are the same missing function.</para>
    ///
    /// <para><b>What is faithfully modelled.</b> The level derivation, the difficulty setting's two
    /// adjustments (<c>Land.as:988-995</c> — <c>earMult</c> below 2, <c>enemyLevel</c> above 2), all seven
    /// biome arms of the <c>tipEnemy</c> selection, the three difficulty bands of <c>setKolEn</c> calls,
    /// the <c>kolEnSpawn</c> guard, <c>kolEnHid</c>, and the two trailing <c>setKolEn</c> corrections.
    /// Draw counts match the oracle arm-for-arm, because <c>setKolEn</c>'s own <c>param4</c> bonus and the
    /// <c>0.15</c> band roll both consume the shared spawn stream. The two <c>globalDif</c> statements
    /// draw nothing, so adding them could not shift the stream.</para>
    ///
    /// <para><b>What is deliberately not modelled, and why.</b> The oracle's <c>Location.budilo</c>
    /// (<c>Location.as:2867</c>) folds <c>earMult</c> into the ALARM radius, squared. The port does not
    /// implement <c>Location.budilo</c> at all — the alarm path it does have is <c>Unit.budilo</c>
    /// (<c>Unit.as</c>), which carries <b>no</b> <c>earMult</c> term. So <c>earMult</c> reaches the
    /// hearing path (<c>Unit.noise</c>, linear) and not the alarm path, which is exactly the oracle's
    /// split. Recorded rather than papered over: adding the term at the alarm site would apply a
    /// difficulty modifier twice over.</para>
    ///
    /// <para><b>Call it once per room, at build time.</b> AS3 calls <c>setLocDif</c> from <c>newLoc</c>
    /// (<c>Land.as:975</c>), i.e. once per constructed room, never per tick — the counts are baked into
    /// the <c>Location</c> and then consumed by <c>setObjects</c>. The port's equivalent seam is
    /// <c>RoomGenerator.GenerateRoom</c>.</para>
    /// </summary>
    public static class LocationDifficultyPlanner
    {
        /// <summary>
        /// AS3 <c>Land.setLocDif(loc, yOffset)</c>. Writes the level fields, <c>tipEnemy</c>, the five
        /// <c>kolEn</c> slots, <c>kolEnSpawn</c> and <c>kolEnHid</c> onto <paramref name="difficulty"/>.
        /// </summary>
        /// <param name="difficulty">The room's difficulty record; mutated in place.</param>
        /// <param name="input">The land-level inputs.</param>
        /// <param name="rng">
        /// The spawn stream. Required — the oracle draws from <c>Math.random()</c> here and the draws are
        /// part of the stream that room layout also uses.
        /// </param>
        public static void Apply(RoomDifficulty difficulty, in LocationDifficultyInput input, IRngService rng)
        {
            if (difficulty == null || rng == null) return;

            int biom = input.Biome;
            float locDifLevel = input.LandDifLevel + input.DifficultyOffset;

            // Land.as:982-987
            difficulty.baseDifficulty = locDifLevel;
            difficulty.lockLevel = locDifLevel * 0.7f;
            difficulty.mechLevel = locDifLevel / 4f;
            difficulty.weaponLevel = 1f + locDifLevel / 4f;
            difficulty.enemyLevel = locDifLevel;

            // ── Land.as:988-995 — the difficulty SETTING's two adjustments, in oracle order and position.
            //    Both statements are draw-free, so they cannot shift the shared spawn stream; the fixture
            //    that pins the draw counts stays valid (T6).
            //
            //    The guard on `enemyLevel` is `> 2`, not `>= 2`, and the step is 2 per level above Normal:
            //    levels 0 and 1 change `earMult` only, level 2 is the identity for both, and 3/4 raise
            //    `enemyLevel` by 2 and 4 respectively. Note `earMult` is a MULTIPLY on the Location's own
            //    default of 1, so a second call would halve it again — which is why this runs once per
            //    room, at build time.
            if (input.GlobalDifficulty < 2) difficulty.earMult *= 0.5f;
            if (input.GlobalDifficulty > 2) difficulty.enemyLevel += (input.GlobalDifficulty - 2) * 2;

            int tipEnemy = input.TipEnemy;

            // ── Land.as:996-1049: tipEnemy selection. Every arm is transcribed with its guard, because
            //    a guard that is false must NOT draw.
            if (biom == 0 && Rand(rng) < 0.25f) tipEnemy = 1;
            if (tipEnemy < 0) tipEnemy = (int)System.Math.Floor(Rand(rng) * 3f);
            if (biom == 1) tipEnemy = 0;
            if (biom == 2 && tipEnemy == 1 && Rand(rng) < locDifLevel / 20f) tipEnemy = 3;
            if (biom == 3) tipEnemy = (int)System.Math.Floor(Rand(rng) * 3f) + 3;
            if (locDifLevel > 12 && (biom == 0 || biom == 2 || biom == 3) && Rand(rng) < 0.1f) tipEnemy = 6;
            if (biom == 4) tipEnemy = 7;
            if (biom == 5)
            {
                tipEnemy = Rand(rng) < 0.3f ? 5 : 8;
            }
            if (biom == 6)
            {
                tipEnemy = Rand(rng) > 0.3f ? 9 : 10;
            }
            if (biom == 11) tipEnemy = 11;

            difficulty.tipEnemy = tipEnemy;

            // ── Land.as:1050-1127: the per-band enemy counts.
            if (locDifLevel < 4f)
            {
                difficulty.SetEnemyCount(1, 3, 5, 2, rng);
                difficulty.SetEnemyCount(2, 2, 4, 0, rng);
                difficulty.SetEnemyCount(3, 3, 4, 2, rng);
                difficulty.SetEnemyCount(4, 1, 2, 0, rng);
                difficulty.SetEnemyCount(5, 1, 4, 2, rng);
                if (tipEnemy == 6)
                {
                    difficulty.SetEnemyCount(2, 1, 3, 0, rng);
                }
                if (difficulty.spawnEnemyCount == 0)
                {
                    if (tipEnemy != 5)
                    {
                        difficulty.SetEnemyCount(RoomDifficulty.SpawnCountSlot, 1, 2, 0, rng);
                    }
                }
                difficulty.hiddenEnemyCount = 0;
            }
            else if (locDifLevel < 10f)
            {
                difficulty.SetEnemyCount(1, 3, 6, 2, rng);

                // One draw, always, in this band — the condition itself consumes it.
                if (Rand(rng) < 0.15f)
                {
                    difficulty.SetEnemyCount(2, 1, 1, 0, rng);
                }
                else if (tipEnemy == 6)
                {
                    difficulty.SetEnemyCount(2, 2, 3, 0, rng);
                }
                else
                {
                    difficulty.SetEnemyCount(2, 2, 5, 0, rng);
                }

                difficulty.SetEnemyCount(3, 3, 5, 2, rng);
                difficulty.SetEnemyCount(4, 2, 3, 1, rng);
                difficulty.SetEnemyCount(5, 2, 4, 2, rng);
                if (difficulty.spawnEnemyCount == 0)
                {
                    if (tipEnemy != 5)
                    {
                        difficulty.SetEnemyCount(RoomDifficulty.SpawnCountSlot, 2, 3, 0, rng);
                    }
                }
                difficulty.hiddenEnemyCount = (int)System.Math.Floor(Rand(rng) * 3f);
            }
            else
            {
                difficulty.SetEnemyCount(1, 4, 6, 2, rng);

                if (Rand(rng) < 0.15f)
                {
                    difficulty.SetEnemyCount(2, 1, 2, 0, rng);
                }
                else if (tipEnemy == 6)
                {
                    difficulty.SetEnemyCount(2, 3, 4, 0, rng);
                }
                else
                {
                    difficulty.SetEnemyCount(2, 3, 6, 0, rng);
                }

                difficulty.SetEnemyCount(3, 4, 7, 2, rng);
                difficulty.SetEnemyCount(4, 2, 4, 1, rng);
                difficulty.SetEnemyCount(5, 3, 6, 2, rng);
                if (difficulty.spawnEnemyCount == 0)
                {
                    if (tipEnemy != 5)
                    {
                        difficulty.SetEnemyCount(RoomDifficulty.SpawnCountSlot, 2, 4, 0, rng);
                    }
                    else if (Rand(rng) > 0.4f)
                    {
                        difficulty.SetEnemyCount(RoomDifficulty.SpawnCountSlot, 1, 3, 0, rng);
                    }
                }
                difficulty.hiddenEnemyCount = (int)System.Math.Floor(Rand(rng) * 4f);
            }

            // ── Land.as:1128-1135: two corrections that run after the bands, in this order.
            if (tipEnemy == 5 || tipEnemy == 10)
            {
                difficulty.SetEnemyCount(2, 1, 3, 1, rng);
            }
            if (biom == 11)
            {
                difficulty.SetEnemyCount(2, 5, 8, 0, rng);
            }
        }

        /// <summary>
        /// One oracle <c>Math.random()</c>. <b>Not</b> <see cref="IRngService.Chance"/> — see
        /// <c>LocationEnemySelector</c>'s equivalent note: <c>Chance</c> short-circuits without drawing at
        /// <c>p &lt;= 0</c> and <c>p &gt;= 1</c>, and <c>locDifLevel / 20</c> reaches 1.
        /// </summary>
        static float Rand(IRngService rng) => rng.NextFloat();
    }
}
