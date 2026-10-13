using PFE.Core.Rng;

namespace PFE.Systems.Map
{
    /// <summary>
    /// The <c>Location</c> fields AS3 <c>randomUnit()</c> branches on
    /// (<c>Location.as:1425-1726</c>), none of which the port had a source for before this type.
    ///
    /// <para><b>Why a dedicated struct rather than reusing <see cref="UnitVariantContext"/>.</b>
    /// <c>randomCid()</c> reads <c>biom</c>, <c>tipEnemy</c>, <c>land.act.conf</c> and <c>landY</c>;
    /// <c>randomUnit()</c> reads <c>biom</c>, <c>tipEnemy</c>, <c>locDifLevel</c>, <c>landX</c> and
    /// <c>landY</c>. The overlap is real but neither is a subset of the other — <c>randomUnit</c> needs
    /// <c>landX</c> and the difficulty, <c>randomCid</c> needs the land config — so folding them into one
    /// struct would mean one of the two carries a field it never reads. <see cref="ToVariantContext"/>
    /// converts where the two do overlap.</para>
    ///
    /// <para><b>Every default is the value that makes its branch <i>not</i> taken.</b>
    /// <see cref="Biome"/> defaults to <see cref="NoBiome"/> (<c>-1</c>), which AS3 biomes
    /// (<c>0..11</c>) never equal; <see cref="TipEnemy"/> to AS3's own <c>-1</c>; and
    /// <see cref="Difficulty"/> to <c>0</c>, which is a legal level and therefore <i>is</i> a branch —
    /// see the note on <c>locDifLevel</c> in <see cref="LocationDifficultyPlanner"/>.</para>
    /// </summary>
    public readonly struct LocationEnemyContext
    {
        /// <summary>"No biome" sentinel — see <see cref="UnitVariantResolver.NoBiome"/>.</summary>
        public const int NoBiome = UnitVariantResolver.NoBiome;

        /// <summary>AS3 <c>Location.biom</c> — the land theme (<c>LandAct.biom</c>).</summary>
        public readonly int Biome;

        /// <summary>AS3 <c>Location.tipEnemy</c> — the room's <c>entip</c>, after <c>setLocDif</c> has
        /// had its say. <c>-1</c> = not set.</summary>
        public readonly int TipEnemy;

        /// <summary>AS3 <c>Location.locDifLevel</c> — the room's difficulty level.</summary>
        public readonly float Difficulty;

        /// <summary>AS3 <c>Location.landX</c> — the room's column in the land grid.</summary>
        public readonly int LandX;

        /// <summary>AS3 <c>Location.landY</c> — the room's row in the land grid.</summary>
        public readonly int LandY;

        /// <summary>AS3 <c>land.act.conf</c>, needed only for the <c>randomCid</c> half.</summary>
        public readonly int LandConfig;

        public LocationEnemyContext(int biome, int tipEnemy, float difficulty, int landX, int landY, int landConfig = -1)
        {
            Biome = biome;
            TipEnemy = tipEnemy;
            Difficulty = difficulty;
            LandX = landX;
            LandY = landY;
            LandConfig = landConfig;
        }

        /// <summary>No context at all — every field sentinelled. Use this, never <c>default</c>.</summary>
        public static readonly LocationEnemyContext NoContext =
            new LocationEnemyContext(NoBiome, -1, 0f, 0, -1);

        /// <summary>
        /// The subset <see cref="UnitVariantResolver.RandomCid"/> reads. <c>LandY</c> is carried through
        /// verbatim because <c>0</c> is a real land row — the whole reason
        /// <see cref="UnitVariantContext"/> documents <c>default</c> as a trap.
        /// </summary>
        public UnitVariantContext ToVariantContext() =>
            new UnitVariantContext(Biome, TipEnemy, LandConfig, LandY);

        /// <summary>
        /// AS3 <c>(this.landX + this.landY) % 2 == 0</c> — the parity test six <c>randomUnit</c> branches
        /// use to alternate between two families down a land. Kept as a named property so the six sites
        /// cannot drift apart.
        /// </summary>
        public bool LandParityEven => (LandX + LandY) % 2 == 0;
    }

    /// <summary>
    /// Port of AS3 <c>Location.randomUnit(param1, param2)</c> (<c>Location.as:1425-1726</c>): the step
    /// that turns one of the five authored enemy <i>point groups</i> into the concrete enemy
    /// <i>family</i> that will be created.
    ///
    /// <para><b>This is the piece whose absence made random rooms empty.</b> The room data authors
    /// <c>enl1</c>/<c>enl2</c>/<c>enf1</c>/<c>enc1</c>/<c>lov</c> marker objects and the oracle groups them
    /// into <c>ups[tipn]</c> (<c>Location.as:681-689</c>); <c>setRandomUnits</c> then calls
    /// <c>createUnit(tipEn[], …)</c> once per count, and <c>createUnit</c>'s <i>first</i> act is
    /// <c>randomUnit()</c> (<c>Location.as:1160</c>). Without it the group id — <c>"enl2"</c> — would be
    /// handed straight to <c>Unit.create</c>, and there is no <c>enl2</c> unit. The port had no
    /// <c>randomUnit</c> at all and no caller for the group ids, so the entire mechanism was dead: the
    /// markers were imported as generic props and no enemy was ever created from them.</para>
    ///
    /// <para><b>Draw order is part of the contract.</b> The spawn stream is shared with room layout, so
    /// each branch's <i>number</i> of <c>Math.random()</c> calls is load-bearing, not just the value it
    /// returns. Every <c>rand()</c> below is one oracle <c>Math.random()</c>, and the short-circuiting
    /// <c>&amp;&amp;</c> / <c>||</c> chains are transcribed with their AS3 precedence intact —
    /// <c>&amp;&amp;</c> binds tighter than <c>||</c>, so
    /// <c>tipEnemy == 2 &amp;&amp; locDifLevel &gt;= 4 || tipEnemy == 7</c> is
    /// <c>(tipEnemy == 2 &amp;&amp; locDifLevel &gt;= 4) || tipEnemy == 7</c>.</para>
    ///
    /// <para><b>An empty return is a real answer.</b> Three branches (<c>enl1</c> in biomes 10/11,
    /// <c>enf1</c> in biome 10, <c>lov</c> in biomes 10/11) return <c>""</c>, and the oracle then falls
    /// back to creating the <i>group id itself</i> as a unit (<c>Location.as:1177-1188</c>). That fallback
    /// is why the caller must distinguish <c>""</c> from a family name rather than treating an empty
    /// string as "nothing to do".</para>
    /// </summary>
    public static class LocationEnemySelector
    {
        /// <summary>
        /// The five point groups, in <c>kolEn</c> index order. AS3
        /// <c>internal var tipEn:Array = ["","enl1","enl2","enf1","enc1","lov"]</c>
        /// (<c>Location.as:221</c>) — index 0 is the unused slot, which is why <c>setRandomUnits</c>
        /// loops from 1.
        /// </summary>
        public static readonly string[] GroupIds = { "", "enl1", "enl2", "enf1", "enc1", "lov" };

        /// <summary>Number of <c>kolEn</c> slots (index 0 unused).</summary>
        public const int GroupCount = 6;

        /// <summary>
        /// AS3 <c>tipEn</c> index for an authored marker id, or <c>0</c> when the id is not a marker.
        ///
        /// <para><b>Why the index is derived from the id.</b> The oracle reads <c>tipn</c> off the
        /// <i>AllData definition row</i> (<c>Location.as:683</c> <c>n = int(xmll.@tipn)</c>), and the
        /// definition rows are <c>enl1→1, enl2→2, enf1→3, enc1→4, lov→5</c>
        /// (<c>AllData.as:4931-4935</c>). The port's imported <c>MapObjectDefinition</c> has no
        /// <c>tipn</c> field, and the id <i>is</i> the unique key of that row, so the table above is the
        /// same information read through the key the port actually has. Adding a <c>tipn</c> field would
        /// require a re-import to populate it and would be a second source of truth for a fixed 5-row
        /// table.</para>
        /// </summary>
        public static int GroupIndexForId(string objectId)
        {
            if (string.IsNullOrEmpty(objectId))
            {
                return 0;
            }

            for (int i = 1; i < GroupIds.Length; i++)
            {
                if (string.Equals(GroupIds[i], objectId, System.StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return 0;
        }

        /// <summary>True when the id is one of the five enemy point groups.</summary>
        public static bool IsGroupId(string objectId) => GroupIndexForId(objectId) != 0;

        /// <summary>
        /// AS3 <c>Location.randomUnit(param1, param2)</c>.
        /// </summary>
        /// <param name="groupId">One of <see cref="GroupIds"/> (<c>enl1</c>/<c>enl2</c>/<c>enf1</c>/<c>enc1</c>/<c>lov</c>).</param>
        /// <param name="context">Biome / tipEnemy / difficulty / land coordinates.</param>
        /// <param name="water">
        /// AS3 <c>param2</c> — true when the target tile holds water. <c>createUnit</c> computes it as
        /// <c>(biom == 1 || biom == 5) &amp;&amp; !param4 &amp;&amp; getTile(x, y).water &gt; 0</c>
        /// (<c>Location.as:1156-1159</c>), i.e. only for biomes 1 and 5 and only when the placement is a
        /// real tile rather than a pixel pair.
        /// </param>
        /// <param name="rng">The spawn stream.</param>
        /// <returns>
        /// The enemy family id to create, or <c>""</c> when the oracle declines (biomes 10/11 and the
        /// biome-10 <c>enf1</c> arm) or when the group id is unrecognised.
        /// </returns>
        public static string RandomUnit(string groupId, in LocationEnemyContext context, bool water, IRngService rng)
        {
            int biom = context.Biome;
            int tipEnemy = context.TipEnemy;
            float locDifLevel = context.Difficulty;
            bool parityEven = context.LandParityEven;

            switch (groupId)
            {
                // Location.as:1430-1542
                case "enl2":
                    if (biom == 10) return "stabpon";
                    if (tipEnemy == 0) return "zombie";
                    if (tipEnemy == 2)
                    {
                        if (biom == 2 && locDifLevel >= 12 && Rand(rng) < Min(locDifLevel / 100f, 0.15f)) return "eqd";
                        if (locDifLevel >= 5 && Rand(rng) < 0.1f) return "landturret";
                        if (locDifLevel >= 6 && Rand(rng) < Min(locDifLevel / 40f, 0.3f)) return "gutsy";
                        if (locDifLevel >= 2 && Rand(rng) < Min(locDifLevel / 10f, 0.5f)) return "protect";
                        return "robot";
                    }
                    if (tipEnemy == 3) return "slaver";
                    if (tipEnemy == 4) return "merc";
                    if (tipEnemy == 5) return "alicorn";
                    if (tipEnemy == 6) return "zebra";
                    if (tipEnemy == 7)
                    {
                        if (Rand(rng) < 0.4f)
                        {
                            if (Rand(rng) < 0.5f) return "gutsy";
                            return "protect";
                        }
                        return "ranger";
                    }
                    if (tipEnemy == 8)
                    {
                        if (Rand(rng) < 0.75f) return "zombie";
                        return "necros";
                    }
                    if (tipEnemy == 9)
                    {
                        if (Rand(rng) < 0.1f) return "hellhound";
                        if (Rand(rng) < 0.07f) return "landturret";
                        return "encl";
                    }
                    if (tipEnemy == 10) return "hellhound";
                    if (tipEnemy == 11)
                    {
                        if (Rand(rng) < 0.3f) return "hellhound";
                        return "encl";
                    }
                    return "raider";

                // Location.as:1543-1609
                case "enl1":
                    if (biom == 10 || biom == 11) return string.Empty;
                    if ((biom == 1 || biom == 5) && water) return "fish";
                    if (biom == 5)
                    {
                        if (Rand(rng) < 0.3f) return "scorp3";
                        return "slime";
                    }
                    if (biom == 6) return "roller";

                    // `tipEnemy == 2 && locDifLevel >= 4 || tipEnemy == 7 || 9 || 10`
                    if ((tipEnemy == 2 && locDifLevel >= 4) || tipEnemy == 7 || tipEnemy == 9 || tipEnemy == 10)
                    {
                        return parityEven ? "roller" : "msp";
                    }

                    if (tipEnemy == 0 || tipEnemy == 5 || tipEnemy == 6)
                    {
                        string chosen;
                        if (locDifLevel >= 2 && Rand(rng) < Min(locDifLevel / 30f, 0.5f))
                        {
                            chosen = "scorp";
                        }
                        else
                        {
                            chosen = parityEven ? "slime" : "ant";
                        }

                        // The rat override is a SECOND draw, taken after the family above is chosen.
                        if (biom == 1 && Rand(rng) < 0.25f)
                        {
                            chosen = "rat";
                        }

                        return chosen;
                    }

                    if (locDifLevel >= 2 && Rand(rng) < Min(locDifLevel / 30f, 0.5f)) return "molerat";
                    if (Rand(rng) < 0.6f) return "tarakan";
                    return "rat";

                // Location.as:1610-1647
                case "enc1":
                    if (biom == 10) return "turret";
                    if ((biom == 1 || biom == 5) && water) return "fish";
                    if (biom == 5)
                    {
                        return parityEven ? "bloodwing" : "slime";
                    }
                    if (biom == 6 || biom == 4) return "cturret";
                    if (tipEnemy == 0 || tipEnemy == 5 || tipEnemy == 6)
                    {
                        if (biom == 1 && Rand(rng) < 0.6f) return "slime";
                        return "bloodwing";
                    }
                    return "turret";

                // Location.as:1648-1682
                case "enf1":
                    if (biom == 10) return string.Empty;
                    if ((biom == 1 || biom == 5) && water) return "fish";
                    if (biom == 5) return "bloat";
                    if (tipEnemy == 0 || tipEnemy == 5) return "bloat";
                    if ((tipEnemy == 1 || tipEnemy == 3 || tipEnemy == 4 || tipEnemy == 6) && locDifLevel >= 3)
                    {
                        return "vortex";
                    }
                    if (tipEnemy == 2 && locDifLevel >= 3) return "spritebot";

                    // `tipEnemy == 11 || 7 || 9 || 10 || tipEnemy == 2 && locDifLevel > 10 && rand < …`
                    if (tipEnemy == 11 || tipEnemy == 7 || tipEnemy == 9 || tipEnemy == 10 ||
                        (tipEnemy == 2 && locDifLevel > 10 && Rand(rng) < Min(locDifLevel / 40f, 0.5f)))
                    {
                        return "dron";
                    }

                    // Falls through with `_loc3_ == ""`.
                    return string.Empty;

                // Location.as:1683-1723
                case "lov":
                    if (biom == 10 || biom == 11) return string.Empty;
                    if (biom == 5) return "slmine";
                    if (tipEnemy == 0)
                    {
                        return parityEven ? "slmine" : "trap";
                    }
                    if ((tipEnemy == 1 || tipEnemy == 3 || tipEnemy == 4 || tipEnemy == 6) && parityEven)
                    {
                        if (locDifLevel >= 10 && Rand(rng) < 0.5f) return "trridge";
                        if (Rand(rng) < 0.5f) return "trplate";
                        return "trcans";
                    }

                    // `(biom == 2 && tipEnemy == 2) || tipEnemy == 7 || tipEnemy == 9`
                    if (((biom == 2 && tipEnemy == 2) || tipEnemy == 7 || tipEnemy == 9) && parityEven)
                    {
                        return "trlaser";
                    }
                    return "mine";
            }

            // The oracle's `default:` is absent, so `_loc3_` stays "" — the same answer the biome-10
            // arms give. Callers must therefore handle "" as "create the group id itself".
            return string.Empty;
        }

        /// <summary>
        /// One oracle <c>Math.random()</c>.
        ///
        /// <para><b>Deliberately not <see cref="IRngService.Chance"/>.</b> That helper short-circuits
        /// without drawing for <c>p &lt;= 0</c> and <c>p &gt;= 1</c>, and several of these thresholds
        /// reach 1 (<c>locDifLevel / 20</c> at level 20, <c>locDifLevel / 10</c> at level 10). AS3 always
        /// draws; a helper that sometimes does not would desynchronise the shared spawn stream at exactly
        /// the deep levels where the most units are placed.</para>
        /// </summary>
        static float Rand(IRngService rng) => rng.NextFloat();

        static float Min(float a, float b) => a < b ? a : b;
    }
}
