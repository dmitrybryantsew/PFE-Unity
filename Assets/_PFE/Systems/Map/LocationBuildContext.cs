namespace PFE.Systems.Map
{
    /// <summary>
    /// The land-level facts a room needs that AS3's <c>Location</c> picks up from <c>Land</c> when it is
    /// constructed — everything <c>Land.newLoc</c> writes onto a fresh <c>Location</c> before
    /// <c>setLocDif</c> runs (<c>Land.as:945-978</c>).
    ///
    /// <para><b>Why this exists.</b> <c>RoomGenerator.GenerateRoom</c> is the port's <c>newLoc</c>, but it
    /// is handed only a template and a grid position. The land's biome, config and difficulty are what
    /// <c>Location.randomUnit</c> and <c>Land.setLocDif</c> branch on, so without them a procedurally
    /// generated room could not resolve a single random enemy. They cannot be read off the template: they
    /// belong to the <c>&lt;land&gt;</c> row, and a template can be shared by several lands.</para>
    ///
    /// <para><b><see cref="None"/> is a real, degraded case, not an error.</b> The port's legacy
    /// <c>WorldBuilder.BuildRandomWorld</c> (the boot-time build, reached from
    /// <c>GameManager.BuildWorldAsync</c>) has no <c>LandDefinition</c> at all — it draws from every loaded
    /// template. It therefore passes <see cref="None"/>, and the planner behaves as the oracle would for a
    /// biome outside <c>0..11</c>: every biome arm is false, <c>tipEnemy</c> takes its
    /// <c>if(tipEnemy &lt; 0)</c> roll, and the counts come from the difficulty-0 band. That is strictly
    /// closer to the oracle than the alternative of placing nothing, and it is recorded in the findings doc
    /// as a follow-up rather than hidden.</para>
    /// </summary>
    public readonly struct LocationBuildContext
    {
        /// <summary>AS3 <c>LandAct.biom</c>. <c>-1</c> = unknown.</summary>
        public readonly int Biome;

        /// <summary>AS3 <c>LandAct.conf</c>. <c>-1</c> = unknown.</summary>
        public readonly int LandConfig;

        /// <summary>
        /// AS3 <c>Land.landDifLevel</c> — <c>max(runDifficulty, act.dif)</c> (<c>Land.as:120-124</c>).
        ///
        /// <para>The port has no run-difficulty setting (<c>Game.triggers["firstroom"]</c>-derived
        /// <c>param3</c>, <c>Game.as:353-361</c>), so callers pass the land's authored
        /// <see cref="PFE.Data.Definitions.Campaign.LandDefinition.baseDifficulty"/>, which is the
        /// <c>act.dif</c> floor the oracle also applies. Consequence: on a long run the port's rooms are
        /// easier than AS3's by the unmodelled <c>max(param3, act.dif) - act.dif</c>.</para>
        /// </summary>
        public readonly float LandDifLevel;

        /// <summary>
        /// AS3 <c>Land.newLoc</c>'s <c>_loc7_</c> — the depth bonus added to the difficulty. Non-zero only
        /// for confs 0/1/2 and only on a procedural land (<c>Land.as:959-974</c>); see
        /// <c>LandLayoutPlanner.DifficultyOffsetFor</c>.
        /// </summary>
        public readonly float DifficultyOffset;

        /// <summary>
        /// AS3 <c>Location.noHolesPlace</c>, which <c>Land.newLoc</c> always passes as <c>this.rnd</c>
        /// (<c>Land.as:947</c>) — true for a procedural land. Gates the "drop enemy points on unplaceable
        /// tiles" rule in <c>setRandomUnits</c>.
        /// </summary>
        public readonly bool NoHolesPlace;

        public LocationBuildContext(
            int biome,
            int landConfig,
            float landDifLevel,
            float difficultyOffset = 0f,
            bool noHolesPlace = false)
        {
            Biome = biome;
            LandConfig = landConfig;
            LandDifLevel = landDifLevel;
            DifficultyOffset = difficultyOffset;
            NoHolesPlace = noHolesPlace;
        }

        /// <summary>Unknown land — see the type remarks. Every field sentinelled or zero.</summary>
        public static readonly LocationBuildContext None =
            new LocationBuildContext(LocationEnemyContext.NoBiome, -1, 0f, 0f, false);

        /// <summary>AS3 <c>land.rnd</c> — a procedural land. Used for <see cref="NoHolesPlace"/>.</summary>
        public bool IsProcedural => NoHolesPlace;
    }
}
