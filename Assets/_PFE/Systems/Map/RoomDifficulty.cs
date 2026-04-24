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
        /// Set enemy count for a specific enemy type.
        /// From AS3: setKolEn(tip, min, max, rnd)
        /// </summary>
        public void SetEnemyCount(int enemyType, int minCount, int maxCount)
        {
            if (enemyType >= 1 && enemyType < enemyCounts.Length)
            {
                // Random count between min and max
                enemyCounts[enemyType] = UnityEngine.Random.Range(minCount, maxCount + 1);
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
