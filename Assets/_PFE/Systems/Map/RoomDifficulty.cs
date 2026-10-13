using System;

namespace PFE.Systems.Map
{
    /// <summary>
    /// Room difficulty and scaling data.
    /// From AS3: Location difficulty properties.
    /// </summary>
    [Serializable]
    public class RoomDifficulty
    {
        /// <summary>
        /// The sentinel <see cref="SetEnemyCount"/> takes to mean "not an enemy group" — AS3
        /// <c>setKolEn(-1, min, max)</c>, which writes <c>kolEnSpawn</c> instead of a
        /// <c>kolEn</c> slot (<c>Location.as:1039-1042</c>).
        /// </summary>
        public const int SpawnCountSlot = -1;

        // Base difficulty
        public float baseDifficulty = 0f;

        // Derived stats (calculated from baseDifficulty)
        public float enemyLevel = 0f;      // Enemy strength
        public float lockLevel = 0f;       // Lock difficulty for doors/containers
        public float mechLevel = 0f;       // Hacking/repair difficulty
        public float weaponLevel = 1f;     // Weapon tier available

        // Enemy counts by type
        // From AS3: kolEn array - [1]=enl1, [2]=enl2, [3]=enf1, [4]=enc1, [5]=lov
        public int[] enemyCounts = new int[6];  // Index 1-5 used

        // Hidden enemies
        public int hiddenEnemyCount = 0;

        /// <summary>
        /// AS3 <c>Location.kolEnSpawn</c> (<c>Location.as:225</c>, default <c>0</c>) — how many
        /// enemies the room's <c>enspawn</c> alarm points hold. Written by
        /// <c>setKolEn(-1, min, max)</c> and consumed by <c>Location.enemySpawn</c>
        /// (<c>Location.as:2977-2998</c>).
        ///
        /// <para><b>This is not a spawn count for the random-enemy pass.</b> It feeds the alarm
        /// points, a different seam, and <c>setLocDif</c> reads it back as a guard
        /// (<c>if(param1.kolEnSpawn == 0)</c>) before deciding whether to seed it.</para>
        /// </summary>
        public int spawnEnemyCount = 0;

        /// <summary>
        /// AS3 <c>Location.tipEnemy</c> (<c>Location.as:217</c>, default <c>-1</c>) — the room's
        /// <c>entip</c> option, then overwritten per biome by <c>Land.setLocDif</c>
        /// (<c>Land.as:996-1049</c>). <c>randomUnit</c> and <c>randomCid</c> both branch on it, so it
        /// is the single biggest input to "which enemy is this room about".
        ///
        /// <para><c>-1</c> is AS3's own "not set" value, not a tier: it is the value the
        /// <c>if(param1.tipEnemy &lt; 0)</c> guard tests, so a defaulted struct/field reproduces the
        /// oracle's behaviour rather than silently selecting tier 0.</para>
        /// </summary>
        public int tipEnemy = -1;

        private static PFE.Core.Rng.IRngService s_spawnRng;

        /// <summary>
        /// Set one enemy-group count, or the alarm spawn count.
        /// Port of AS3 <c>Location.setKolEn(param1, param2, param3, param4 = 0)</c>
        /// (<c>Location.as:1037-1051</c>).
        ///
        /// <code>
        /// if(param1 == -1) { this.kolEnSpawn = param2 + floor(random * (param3 - param2 + 1)); }
        /// else {
        ///    this.kolEn[param1] = param2 + floor(random * (param3 - param2 + 1));
        ///    if(param4 &gt; 0 &amp;&amp; random &lt; 0.2) { this.kolEn[param1] += param4; }
        /// }
        /// </code>
        ///
        /// <para><b>The two arms draw a different number of times.</b> The <c>-1</c> arm draws once;
        /// the normal arm draws once <i>and</i> a second time only when <paramref name="bonus"/> is
        /// positive. The spawn stream is shared with room layout, so getting the count wrong shifts
        /// every later placement in the land — no error, no warning. This is the same hazard the
        /// <c>ResolveDiggerTier</c> comment in <c>RoomPopulator</c> describes.</para>
        ///
        /// <para><b>The range is inclusive on both ends.</b> AS3's
        /// <c>floor(random * (max - min + 1))</c> yields <c>[min, max]</c>, which is why
        /// <c>Range(min, max + 1)</c> is the right call against this port's half-open
        /// <see cref="PFE.Core.Rng.IRngService.Range(int,int)"/>.</para>
        /// </summary>
        /// <param name="enemyType">
        /// 1..5 (the <c>kolEn</c> slots: 1=<c>enl1</c>, 2=<c>enl2</c>, 3=<c>enf1</c>, 4=<c>enc1</c>,
        /// 5=<c>lov</c>), or <see cref="SpawnCountSlot"/> for <c>kolEnSpawn</c>. Any other value is
        /// ignored — AS3 would write past the array, which is not behaviour worth reproducing.
        /// </param>
        /// <param name="bonus">AS3 <c>param4</c>: a 20 %-chance addition. <c>0</c> means "no second draw".</param>
        public void SetEnemyCount(
            int enemyType,
            int minCount,
            int maxCount,
            int bonus = 0,
            PFE.Core.Rng.IRngService rng = null)
        {
            var r = rng ?? (s_spawnRng ??= new PFE.Core.Rng.PcgRngService().GetStream(PFE.Core.Rng.RngStream.Spawn));

            if (enemyType == SpawnCountSlot)
            {
                // No bonus draw on this arm — see the oracle excerpt above.
                spawnEnemyCount = minCount + r.Range(0, maxCount - minCount + 1);
                return;
            }

            if (enemyType >= 1 && enemyType < enemyCounts.Length)
            {
                int count = minCount + r.Range(0, maxCount - minCount + 1);

                // `random < 0.2` is written as an explicit draw rather than Chance(0.2f): the count
                // matters, and Chance() short-circuits for p <= 0 / p >= 1 without drawing.
                if (bonus > 0 && r.NextFloat() < 0.2f)
                {
                    count += bonus;
                }

                enemyCounts[enemyType] = count;
            }
        }

        /// <summary>
        /// Get enemy count for a specific type.
        /// </summary>
        public int GetEnemyCount(int enemyType)
        {
            if (enemyType >= 1 && enemyType < enemyCounts.Length)
            {
                return enemyCounts[enemyType];
            }
            return 0;
        }

        /// <summary>
        /// Get total enemy count (excluding hidden).
        /// </summary>
        public int GetTotalEnemyCount()
        {
            int total = 0;
            for (int i = 1; i < enemyCounts.Length; i++)
            {
                total += enemyCounts[i];
            }
            return total;
        }
    }
}
