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

        /// <summary>
        /// AS3 <c>Location.wrad</c> (<c>Location.as:159</c>, <c>:379-380</c>, <c>:408-412</c>) — the
        /// radiation a <b>submerged</b> player absorbs, added to the room's own <see cref="radiation"/>
        /// only while <c>inWater</c> (<c>UnitPlayer.as:906-910</c>).
        ///
        /// <para><b>The oracle's default is 1, not 0</b> (<c>LandAct.as:62</c>,
        /// <c>Location.as:159</c>), so a room that authors no <c>wrad</c> and inherits no land default
        /// still radiates 1 while you swim in it. That is why the converter reads it with a
        /// <c>"1"</c> default rather than the <c>"0"</c> every other float option uses.</para>
        ///
        /// <para>Measured in the oracle's room data: <b>40 of 51</b> authored <c>wrad</c> values are
        /// non-zero, up to 30 — water is the commonest radiation source in the game, not the barrels.
        /// </para>
        /// </summary>
        public float waterRadiation = 1f;

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
