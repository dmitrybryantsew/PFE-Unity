using System;

namespace PFE.Systems.Map
{
    /// <summary>
    /// Room environment settings (water, lava, lighting, etc.).
    /// From AS3: Location environment properties.
    /// </summary>
    [Serializable]
    public class RoomEnvironment
    {
        // Audio
        public string musicTrack = ""; // empty = keep current track; set to a valid ID from MusicCatalog

        // Visual
        public string colorScheme = "";
        public string backgroundColorScheme = "";
        public string backgroundWall = "";
        public int backgroundForm = 0;
        public bool transparentBackground = false;
        public bool hasSky = false;
        public int borderType = 1;
        public bool noBlackReveal = false;
        public float visibilityMultiplier = 1f;
        public int lightsOn = 0;
        public bool returnsToDarkness = false;

        // Water/Lava
        public int waterLevel = 100;        // Pixel Y position (from bottom)
        public int waterType = 0;           // 0=none, 1=water, 2=lava
        public float waterOpacity = 0f;     // 0-1
        public float waterDamage = 0f;      // Damage per second in fluid
        public int waterDamageType = 7;     // Type of damage

        // Environmental hazards
        public float radiation = 0f;        // Radiation level
        public float radiationDamage = 0f;  // Damage per second

        // Lighting
        public int darkness = 0;            // 0-255, darkness level

        /// <summary>
        /// Check if room has water or lava.
        /// </summary>
        public bool HasWater()
        {
            return waterType > 0;
        }

        /// <summary>
        /// Check if water/lava causes damage.
        /// </summary>
        public bool WaterDamages()
        {
            return waterDamage > 0;
        }

        /// <summary>
        /// Check if room has radiation.
        /// </summary>
        public bool HasRadiation()
        {
            return radiation > 0;
        }

        /// <summary>
        /// Check if room is dark.
        /// </summary>
        public bool IsDark()
        {
            return darkness > 0;
        }
    }
}
