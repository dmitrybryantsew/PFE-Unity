using System;

namespace PFE.Systems.Loot
{
    /// <summary>
    /// The pure policy behind AS3 <c>Unit.dropLoot()</c> (<c>Unit.as:4450-4467</c>): which loot table a
    /// dying unit names, and which of the two <b>bonus</b> drops it is entitled to.
    ///
    /// <para><b>The whole of <c>dropLoot</c> is three decisions.</b> <c>Unit.as:4450-4467</c> reads:</para>
    /// <list type="number">
    ///   <item><description><c>inter.loot()</c> — which for a <c>Unit</c> owner routes to
    ///     <c>LootGen.lootDrop(loc, X, Y, cont, hero)</c> (<c>Interact.as:2007-2010</c>). That is the
    ///     table roll, and it is the substantive half: 47 unit tables.</description></item>
    ///   <item><description><c>hero &gt; 0 &amp;&amp; opt.robot != true &amp;&amp; isrnd(0.75)</c> → one
    ///     <c>essence</c>.</description></item>
    ///   <item><description><c>pers.dropTre &gt; 0 &amp;&amp; xp &gt; 0</c> and
    ///     <c>Math.random() &lt; dropTre * xp / 4000</c> → one <c>gem1..3</c>.</description></item>
    /// </list>
    ///
    /// <para><b>Why the table key is <i>resolved</i> rather than read.</b> AS3 does not store the drop
    /// key anywhere on the unit row: <c>Unit.as:1005-1009</c> assigns <c>inter.cont = mid</c> (the unit's
    /// id) and <i>then</i> lets the row's own <c>@cont</c> override it. So a <c>raider5</c> — which
    /// authors <c>cont='raider'</c> — rolls the <c>raider</c> table, while a <c>rat</c> — which authors
    /// nothing — rolls the table keyed <c>"rat"</c>, i.e. <b>its own id</b>. That is exactly why
    /// <c>lootDrop</c> carries ids like <c>rat</c>, <c>slime</c>, <c>ant1</c>, <c>bloodwing</c>,
    /// <c>necros</c>, <c>ebloat</c> beside names like <c>raider</c> and <c>zombie</c>.</para>
    ///
    /// <para><b>There is no parent chain.</b> The tempting third step — "else the unit's
    /// <c>parent</c>" — is not in the oracle. <c>node0</c> is looked up by <c>mid</c>
    /// (<c>Unit.as:996</c>, <c>AllData.d.unit.(@id == mid)[0]</c>) and only <i>that</i> row's
    /// <c>@cont</c> is read; <c>Unit.as</c> never reads <c>parent</c> at all. So a variant that authors
    /// no <c>cont</c> falls to its own id, not to its parent's key.</para>
    /// </summary>
    public static class UnitDeathDrop
    {
        /// <summary>AS3 <c>isrnd(0.75)</c> — the essence drop chance (<c>Unit.as:4456</c>).</summary>
        public const float EssenceChance = 0.75f;

        /// <summary>AS3 <c>/4000</c> — the xp divisor in the treasure-gem chance (<c>Unit.as:4462</c>).</summary>
        public const float GemXpDivisor = 4000f;

        /// <summary>The item id <c>LootGen.lootId(…, "essence")</c> names (<c>Unit.as:4458</c>).</summary>
        public const string EssenceItemId = "essence";

        /// <summary>AS3 <c>Math.floor(Math.random() * 3 + 1)</c> — three gem variants (<c>:4464</c>).</summary>
        public const int GemVariants = 3;

        /// <summary>
        /// AS3 <c>Unit.as:1005-1009</c> — the table a unit drops from.
        ///
        /// <para><paramref name="authoredCont"/> is the unit row's own <c>@cont</c>
        /// (<see cref="PFE.Data.Definitions.UnitDefinition.lootTableKey"/>).</para>
        ///
        /// <para><paramref name="legacyControllerId"/> is a <b>transitional</b> input, and it is the one
        /// non-oracle step here. <c>UnitDataImporter</c> has always written <c>cont</c> into
        /// <c>UnitDefinition.controllerId</c> as well — the "lookalike" the <c>RoomInstance</c> doc warns
        /// about — so an asset that has not been re-imported since <c>lootTableKey</c> was added carries
        /// the key <i>only</i> there. Reading it here is what lets the feature work on existing assets
        /// without an editor round-trip. It is gated on <see cref="LootRoller.IsKnownUnitKey"/>, so it can
        /// never invent a table, and it is <b>checked after</b> the authored value, so a re-import always
        /// wins. <b>Delete this branch once every unit asset has been re-imported.</b></para>
        ///
        /// <para><b>Not a fallback to <c>parent</c>.</b> See the class doc: the oracle reads the unit's own
        /// row, so the last step is the unit's own id.</para>
        /// </summary>
        public static string ResolveLootTableKey(string authoredCont, string legacyControllerId, string unitId)
        {
            if (!string.IsNullOrEmpty(authoredCont)) return authoredCont;

            if (!string.IsNullOrEmpty(legacyControllerId) && LootRoller.IsKnownUnitKey(legacyControllerId))
            {
                return legacyControllerId;
            }

            return unitId ?? string.Empty;
        }

        /// <summary>
        /// AS3 <c>this.hero &gt; 0 &amp;&amp; this.opt.robot != true &amp;&amp; this.isrnd(0.75)</c>
        /// (<c>Unit.as:4456</c>). <c>hero</c> is a runtime tier — <b>no row in <c>AllData.as</c> authors
        /// it</b>; it arrives on the placement (<c>Unit.as:645</c>, <c>this.hero = param3.@hero</c>) or is
        /// set by a boss spawner (<c>setHero</c>, <c>:1626</c>). 33 room assets under
        /// <c>Resources/Rooms</c> author <c>hero=</c> today, so the gate is live.
        /// </summary>
        public static bool ShouldDropEssence(int heroTier, bool isRobot, float rnd)
            => heroTier > 0 && !isRobot && rnd < EssenceChance;

        /// <summary>
        /// AS3 <c>World.w.pers &amp;&amp; World.w.pers.dropTre &gt; 0 &amp;&amp; this.xp &gt; 0</c> and
        /// <c>Math.random() &lt; World.w.pers.dropTre * this.xp / 4000</c> (<c>Unit.as:4460-4462</c>).
        ///
        /// <para>The comparison is a plain <c>&lt;</c> against a <b>float</b> ratio, not an integer
        /// threshold: <c>dropTre</c> is a perk level (1..n) and <c>xp</c> varies per unit, so a boss with
        /// <c>xp=850</c> at <c>dropTre=5</c> is a 1.06 chance (always), while a <c>rat</c> with
        /// <c>xp=20</c> is 0.025.</para>
        /// </summary>
        public static bool ShouldDropGem(float dropTre, int xp, float rnd)
            => dropTre > 0f && xp > 0 && rnd < dropTre * xp / GemXpDivisor;

        /// <summary>
        /// AS3 <c>"gem" + Math.floor(Math.random() * 3 + 1)</c> (<c>Unit.as:4464</c>) — one of
        /// <c>gem1</c>/<c>gem2</c>/<c>gem3</c>. <c>Math.random()</c> is <c>[0,1)</c>, so the floor is
        /// <c>1..3</c> and the id is never <c>gem4</c>.
        /// </summary>
        public static string RollGemId(float rnd)
        {
            int variant = (int)Math.Floor(rnd * GemVariants + 1f);
            if (variant < 1) variant = 1;
            if (variant > GemVariants) variant = GemVariants;
            return "gem" + variant;
        }
    }
}
